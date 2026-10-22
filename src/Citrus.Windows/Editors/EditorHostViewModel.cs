using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Image;
using Citrus.Core;
using Citrus.Core.Editing;

namespace Citrus.Windows.Editors;

public sealed class EditorSourceRow : INotifyPropertyChanged
{
    public string RawPath { get; }
    public bool Imported { get; }
    public SourceSnapshot? Source { get; internal set; }
    public SizeI Size { get; internal set; }
    public string? Error { get; internal set; }
    public EditorHistory? History { get; internal set; }
    public EditorDocument? Draft { get; internal set; }
    public bool Dirty { get; internal set; }
    private ImageResizeIntent? resizeIntent;
    private readonly Dictionary<ImageGeometry,ImageResizeIntent?> resizeIntents = [];
    public ImageResizeIntent? ResizeIntent
    {
        get => resizeIntent;
        internal set { resizeIntent = value; RememberResize(); }
    }
    internal void RememberResize()
    {
        if (Draft?.Recipe is not ImageRecipe image) return;
        resizeIntents[image.Geometry] = resizeIntent;
        if (resizeIntents.Count > 100) resizeIntents.Remove(resizeIntents.Keys.First());
    }
    internal void RestoreResize() { resizeIntent = Draft?.Recipe is ImageRecipe image && resizeIntents.TryGetValue(image.Geometry,out var value) ? value : null; }
    private bool included = true;
    public bool Included { get => included; set { included = value; Changed(); } }
    public string Label => Path.GetFileName(RawPath);
    public string Detail => Error ?? $"{Size.Width} × {Size.Height}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public EditorSourceRow(string rawPath, bool imported) { RawPath = rawPath; Imported = imported; }
    internal void Changed() { PropertyChanged?.Invoke(this, new(null)); }
}

/// <summary>Owns recorded sources and per-root drafts, independently of the selected preview.</summary>
public sealed class EditorHostViewModel(EditorOpenRequest request, ImageEditorServices services)
{
    public EditorOpenRequest Request { get; } = request;
    public ObservableCollection<EditorSourceRow> Sources { get; } = [];
    public EditorSourceRow? Selected { get; set; }
    public string? Destination { get; set; } = request.DestinationDirectory;
    public bool Dirty => Sources.Any(r => r.Dirty);
    private bool accepted;
    private readonly List<EditorImport> auxiliaryImports = [];
    /// <summary>Explicit inset input, never a root row or part of the initial drag payload.</summary>
    public async Task<SourceSnapshot> ImportAuxiliaryAsync(string path,CancellationToken ct)
    {
        if(Selected?.Draft is null)throw new ArgumentException("Select a readable image before importing an inset.");
        if (Request.InitialPaths.Any(p=>SamePath(p,path)) || Sources.Any(r=>SamePath(r.RawPath,path)))
            throw new ArgumentException("This image is already included. Choose another inset image.");
        var existing=auxiliaryImports.FirstOrDefault(i=>SamePath(i.Source.Path,path));

        var capture=(await services.Sources.CaptureAsync([path],ct)).Single();
        var source=capture.Snapshot??throw new IOException(capture.Error??"Could not load inset image.");
        await services.InspectAsync(source,ct);ct.ThrowIfCancellationRequested();
        if(existing is not null)
        {
            if(existing.Source.Sha256.Equals(source.Sha256,StringComparison.OrdinalIgnoreCase)&&existing.Source.Length==source.Length){return existing.Source;}
            if(Sources.Any(r=>r.Draft is {} d&&EditorRecipeReferences.Sources(d.Recipe).Contains(existing.Source.Id)))
                throw new ArgumentException("Inset image changed. Delete its layers, then import it again.");
            auxiliaryImports.Remove(existing);
        }
        auxiliaryImports.Add(new(source,EditorImportRole.Auxiliary));return source;
    }
    public async Task ValidateLayerDependenciesAsync(CancellationToken ct)
    {
        // Exact file+face and glyph coverage are checked while the host can still offer explicit resolution.
        var documents=Sources.Where(r=>r.Included&&r.Draft is not null).Select(r=>r.Draft!).ToArray();
        await Task.Run(()=>
        {
            foreach(var document in documents) foreach(var layer in ((ImageRecipe)document.Recipe).Annotations)
            {
                ct.ThrowIfCancellationRequested();
                string? text=layer.Geometry switch {TextGeometry t=>t.Text,NumberGeometry n=>n.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),_=>null};
                if(text is null)continue;
                var font=document.Fonts.Single(f=>f.Id==layer.Style.Font!.ArtifactId);var issues=FontTextValidator.Validate(font,text);
                if(!issues.IsEmpty)throw new ArgumentException(string.Join(" ",issues.Select(i=>i.Message)));
            }
        },ct);
    }
    public async Task CaptureAsync(ImmutableArray<string> paths, bool imported, CancellationToken ct)
    {
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (auxiliaryImports.Any(i=>SamePath(i.Source.Path,path)))throw new ArgumentException("This image is already used as an inset. Choose another image.");
            if (Sources.Any(r => SamePath(r.RawPath,path)))
                throw new ArgumentException("This file is already listed: " + path);
            var row = new EditorSourceRow(path, imported); Sources.Add(row);
            var capture = (await services.Sources.CaptureAsync([path], ct)).Single();
            row.Source = capture.Snapshot; row.Error = capture.Error;
            if (row.Source is { } source)
            {
                try
                {
                    row.Size = await services.InspectAsync(source, ct);
                    var document = CreateDocument(source, row.Size, Request);
                    row.History = new(document); row.Draft = document; row.RememberResize();
                    if (Request.EntryTool == "Resize" && (Request.InitialOptions.Width is not null || Request.InitialOptions.Height is not null)) row.ResizeIntent = new(Request.InitialOptions.Width, Request.InitialOptions.Height, Request.InitialOptions.PreserveAspectRatio, true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException) { row.Error = error.Message; }
            }
            row.Changed(); Selected ??= row.Draft is not null ? row : null;
        }
    }
    public async Task ReloadAsync(EditorSourceRow row, CancellationToken ct)
    {
        if (!Sources.Contains(row)) throw new ArgumentException("Unknown source row.");
        var capture = (await services.Sources.CaptureAsync([row.RawPath], ct)).Single();
        row.Source = capture.Snapshot; row.Error = capture.Error; row.Draft = null; row.History = null; row.ResizeIntent = null;
        if (row.Source is { } source)
        {
            try
            {
                row.Size = await services.InspectAsync(source, ct);
                row.Draft = CreateDocument(source,row.Size,Request); row.History = new(row.Draft); row.Dirty = false;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException) { row.Error = error.Message; }
        }
        row.Changed();
    }
    private static bool SamePath(string first,string second)
    {
        try { return Path.GetFullPath(first).Equals(Path.GetFullPath(second),StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return first.Equals(second,StringComparison.OrdinalIgnoreCase); }
    }
    internal static EditorDocument CreateDocument(SourceSnapshot source, SizeI size, EditorOpenRequest request)
    {
        var options = request.InitialOptions;
        if (request.EntryTool == "Rotate" && options.RotationDegrees is not (90 or 180 or 270)) throw new ArgumentException("Choose a clockwise rotation of 90, 180 or 270 degrees.");
        var recipe = new ImageRecipe(source.Id, new(request.EntryTool == "Rotate" ? ((options.RotationDegrees / 90) % 4 + 4) % 4 : 0, false, false, null, null, true),
            new(1, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, PhotoEffect.None, 1), [], null, [], []);
        var document = new EditorDocument(1, 0, [source], [], recipe,
            new(Path.GetExtension(source.Path)[1..].ToLowerInvariant(), request.DestinationDirectory, Path.GetFileNameWithoutExtension(source.Path), new QualityGoal(options.Quality), MetadataPolicy.PreserveSupported, new(255,255,255,255)));
        if(request.EntryTool=="Compress"&&options.Quality==new OperationOptions().Quality)document=document with{Export=document.Export with{Compression=new ReductionGoal(50)}};
        if(request.EntryTool=="Background")
        {
            recipe=recipe with{Canvas=DefaultBackground(size)};
            document=document with{Recipe=recipe};
        }
        if (request.EntryTool == "Resize" && (options.Width is not null || options.Height is not null))
            document = ImageBatchMapper.Map(new(document, size, ImageBatchGeometry.Relative, new(options.Width, options.Height, options.PreserveAspectRatio, true), false, true), document, size);
        Validate(document, size); return document;
    }
    private static CanvasSpec DefaultBackground(SizeI photo)
    {
        int padding=Math.Max(0,Math.Min(160,(16384-Math.Max(photo.Width,photo.Height))/2));
        while(padding>0&&(long)(photo.Width+padding*2)*(photo.Height+padding*2)>64_000_000)padding--;
        return new(new(photo.Width+padding*2,photo.Height+padding*2),new GradientBackground(45,[new(0,new(255,105,155,255)),new(.5,new(255,175,92,255)),new(1,new(255,231,38,255))]),new(padding,padding,photo.Width,photo.Height),ImageFit.Contain,24,new(0,8,24,new(0,0,0,64)));
    }
    internal void AddDefaultBackground()
    {
        if(Selected is not {Draft:{Recipe:ImageRecipe {Canvas:null} image} document} row)return;
        var photo=ImageGeometryMapper.Resolve(image,row.Size).OutputSize;
        Change(ImageCanvasPlacement.Change(document,photo,DefaultBackground(photo)));
    }
    public void Change(EditorDocument document, bool transient = false)
    {
        var row = Selected ?? throw new InvalidOperationException("Select an image.");
        Validate(document, row.Size);
        if (!transient) { row.History!.Commit(document, Guid.NewGuid().ToString()); row.Draft = row.History.Current; row.Dirty = true; }
        else row.Draft = document;
        if (!transient) row.RememberResize();
    }
    public void CommitGesture()
    {
        if (Selected is not { Draft: { } draft, History: { } history } row || draft == history.Current) return;
        history.Commit(draft, Guid.NewGuid().ToString()); row.Draft = history.Current; row.Dirty = true; row.RememberResize();
    }
    public void CancelGesture() { if (Selected?.History is { } history) Selected.Draft = history.Current; }
    public void Undo() { CancelGesture(); if (Selected?.History?.Undo() == true) { Selected.Draft = Selected.History.Current; Selected.RestoreResize(); } }
    public void Redo() { CancelGesture(); if (Selected?.History?.Redo() == true) { Selected.Draft = Selected.History.Current; Selected.RestoreResize(); } }
    public void Reset()
    {
        if (Selected is not { History: { } history, Draft: { Recipe: ImageRecipe image } document } row) return;
        // Entry presets apply only on opening. Reset is an undoable neutral recipe for this root.
        var identity = document with
        {
            Sources=[..document.Sources.Where(s=>s.Id==image.SourceId)],Fonts=[],
            Recipe = new ImageRecipe(image.SourceId,new(0,false,false,null,null,false),
                new(1,0,0,0,1,0,0,1,0,0,0,0,0,0,0,0,0,0,0,1,PhotoEffect.None,1),[],null,[],[]),
            Export = document.Export with { Compression = new QualityGoal(85) }
        };
        history.Commit(identity,Guid.NewGuid().ToString()); row.Draft = history.Current; row.ResizeIntent = null; row.Dirty = true;
    }
    public void ApplyToAll(ImageBatchGeometry geometry, ImageResizeIntent? resize)
    {
        CommitGesture(); var selected = Selected ?? throw new InvalidOperationException("Select an image.");
        var template = new ImageBatchTemplate(selected.Draft!, selected.Size, geometry, resize, false, true);
        // Prepare and validate every participating document before changing any history.
        var mapped = Sources.Where(r => r.Included && r != selected).Select(r =>
        {
            if (r.Draft is null || r.Error is not null) throw new ArgumentException(r.Label + ": exclude or reload this unreadable source.");
            var document = ImageBatchMapper.Map(template, r.Draft, r.Size); Validate(document, r.Size); return (Row: r, Document: document);
        }).ToArray();
        foreach (var item in mapped) { item.Row.History!.Commit(item.Document, Guid.NewGuid().ToString()); item.Row.Draft = item.Row.History.Current; item.Row.Dirty = true; item.Row.ResizeIntent = resize; }
    }
    public EditorSubmissionBatch Accept()
    {
        if (accepted) throw new InvalidOperationException("This editor has already returned its batch.");
        CommitGesture();
        var included = Sources.Where(r => r.Included).ToArray();
        if (included.Length == 0) throw new ArgumentException("Include at least one image.");
        foreach (var row in included)
        {
            if (row.Error is not null || row.Draft is null || row.Source is null) throw new ArgumentException(row.Label + ": " + (row.Error ?? "Image did not finish loading.") + " Exclude it to continue.");
            Validate(row.Draft, row.Size);
        }
        var batch = new EditorSubmissionBatch(Request,
            [.. Sources.Where(r => !r.Imported && r.Source is not null).Select(r => r.Source!)],
            [.. Sources.Where(r => !r.Imported && !r.Included).Select(r => r.RawPath)],
            [.. Sources.Where(r => r.Imported && r.Source is not null).Select(r => new EditorImport(r.Source!, EditorImportRole.Root)).Concat(auxiliaryImports)],
            [.. included.Select(r => r.Source!.Id)],
            [.. included.Select(r =>
            {
                var draft=r.Draft!;var references=EditorRecipeReferences.Sources(draft.Recipe).ToHashSet();var fonts=EditorRecipeReferences.Fonts(draft.Recipe).ToHashSet();
                var frozen=draft with {Sources=[..draft.Sources.Where(s=>references.Contains(s.Id))],Fonts=[..draft.Fonts.Where(f=>fonts.Contains(f.Id))],Export=draft.Export with {DestinationDirectory=Destination}};
                return new EditorSubmissionItem(Guid.NewGuid(),[r.Source!.Id],frozen);
            })]);
        accepted = true; return batch;
    }
    internal static void Validate(EditorDocument document, SizeI size)
    {
        var errors = EditorRecipeValidator.Validate(document);
        if (!errors.IsEmpty) throw new ArgumentException(string.Join("; ", errors.Select(e => e.FieldPath + ": " + e.Message)));
        ImageGeometryMapper.Resolve((ImageRecipe)document.Recipe, size);
    }
}
