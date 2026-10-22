using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Citrus.Conversion;
using Citrus.Conversion.Editing.Image;
using Citrus.Core.Editing;
using Citrus.Windows.Editors.Image;
using Microsoft.Win32;

namespace Citrus.Windows.Editors;

public partial class EditorHostWindow : Window
{
    public static IReadOnlyList<string> Tools { get; } = Array.AsReadOnly(new[] { "Adjust", "Crop", "Resize", "Rotate", "Annotate", "Background", "Manual masks", "Compress" });
    private readonly ImageEditorServices services;
    private readonly ImageRecipeRenderer renderer;
    private readonly InstalledFontCatalog fontCatalog;
    private readonly EditorSession session;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? previewWork;
    private EditorHostViewModel? model;
    private ImageEditorView? inspector;
    private AnnotationInspector? layerInspector;
    private EditorSubmissionBatch? accepted;
    private long generation;
    private long textMetricsGeneration;
    private double previewZoom=1;
    private readonly ImageTextMetricsCache textMetrics=new();
    private bool closed, loading, shown, changingSelection;
    private string previousTool = "Adjust";
    internal EditorHostViewModel? Model => model;
    public EditorHostWindow(ImageEditorServices services)
    {
        this.services = services; renderer = services.CreateSessionRenderer(); session = new(renderer);fontCatalog=new(lifetime.Token);
        InitializeComponent(); Citrus.Windows.Presentation.WindowChromeSetup.Apply(this); DataContext = this; ToolPicker.ItemsSource = Tools;
        CropHandles.Changed += CropChanged;
        LayerHandles.Changed += LayerChanged;
        LayerHandles.TextBounds=GetTextBounds;
        LayerHandles.SelectionChanged += RebuildInspector;
        LayerHandles.Error += message=>ErrorText.Text=message;
        LayerHandles.ViewChanged += (zoom,pan)=>
        {
            var transform=new TransformGroup();transform.Children.Add(new ScaleTransform(zoom,zoom));transform.Children.Add(new TranslateTransform(pan.X,pan.Y));PreviewImage.RenderTransform=transform;
        };
        PreviewArea.SizeChanged += (_, _) => { if (DetailPreview.IsChecked == true) RefreshPreview(); };
        DpiChanged += (_,_) => { if(DetailPreview.IsChecked==true)RefreshPreview(); };
        PreviewKeyDown += KeyPressed; Closing += CheckClose;
        Closed += (_, _) => { closed = true; session.Close(); lifetime.Cancel(); previewWork?.Cancel(); textMetrics.Dispose();fontCatalog.Dispose(); };
        // Attach after all lifetime/placement wiring; ordinary windows use the shared controller and button springs.
        (Application.Current as App)?.AttachPresentation(this, Chrome);
    }
    public async Task<EditorSubmissionBatch?> ShowEditorAsync(EditorOpenRequest request, CancellationToken ct)
    {
        if (shown) throw new InvalidOperationException("An editor window can be shown once."); shown = true;
        model = new(request, services); SourceList.ItemsSource = model.Sources;
        Heading.Text = request.Operation.Label; Title = request.Operation.Label + " · Citrus";
        ToolPicker.SelectedItem = Tools.Contains(request.EntryTool) ? request.EntryTool : "Adjust";
        Loaded += LoadInitial;
        using var registration = ct.Register(() => Dispatcher.BeginInvoke(new Action(() => { if(closed)return;lifetime.Cancel();Close(); })));
        try { ct.ThrowIfCancellationRequested(); ShowDialog(); return accepted; }
        finally
        {
            session.Close(); lifetime.Cancel(); previewWork?.Cancel();
            await renderer.DisposeAsync(); previewWork?.Dispose(); lifetime.Dispose();
        }
        async void LoadInitial(object sender, RoutedEventArgs e)
        {
            Loaded -= LoadInitial;
            await Capture(request.InitialPaths, false);
        }
    }
    private async Task Capture(ImmutableArray<string> paths, bool imported)
    {
        loading = true; ExportButton.IsEnabled = false; PreviewStatus.Text = "Loading images…";
        try
        {
            await model!.CaptureAsync(paths, imported, lifetime.Token);
            foreach (var row in model.Sources) { row.PropertyChanged -= RowChanged; row.PropertyChanged += RowChanged; }
            SourceList.SelectedItem = model.Selected ?? model.Sources.FirstOrDefault();
            RebuildInspector(); RefreshPreview(); UpdateSummary();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!closed) ErrorText.Text = error.Message; }
        finally { loading = false; if (!closed) { ExportButton.IsEnabled = true; UpdateSummary(); } }
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e) => UpdateSummary();
    private void UpdateSummary()
    {
        if (model is null) return;
        SourceHeading.Text=model.Selected?.Label??"Images";
        IncludedText.Text = $"{model.Sources.Count(r => r.Included)} of {model.Sources.Count} included";
        DestinationText.Text = model.Destination ?? "Beside originals";
        UpdateExportAvailability();
    }
    private bool BackgroundsReady => model is not null && model.Sources.Any(row=>row.Included) && model.Sources.Where(row=>row.Included).All(row=>row.Draft?.Recipe is ImageRecipe {Canvas:not null});
    private void UpdateExportAvailability()
    {
        bool missingBackground=Tool=="Background"&&!BackgroundsReady;
        ExportButton.IsEnabled=!loading&&accepted is null&&!missingBackground;
        string reason=missingBackground?"Add a background to every included image. Use Apply to all, or exclude images without one.":"";
        ExportButton.ToolTip=reason.Length==0?null:reason;
        ToolTipService.SetShowOnDisabled(ExportButton,true);
        System.Windows.Automation.AutomationProperties.SetHelpText(ExportButton,reason);
    }
    private void SelectSource(object sender, SelectionChangedEventArgs e)
    {
        if (model is null || changingSelection) return;
        try { FlushInspector(); }
        catch (ArgumentException error)
        {
            ErrorText.Text = error.Message; changingSelection = true; SourceList.SelectedItem = model.Selected; changingSelection = false; return;
        }
        LayerHandles.CancelDrag(); model.CommitGesture(); model.Selected = SourceList.SelectedItem as EditorSourceRow; CropHandles.AspectRatio = null; LayerHandles.Select(null); LayerHandles.ResetView();
        PreviewImage.Source = null; CropHandles.CancelDrag(); RebuildInspector(); RefreshPreview(); UpdateSummary();
    }
    private void InclusionChanged(object sender, RoutedEventArgs e) => UpdateSummary();
    private string Tool => ToolPicker.SelectedItem as string ?? "Adjust";
    private void ToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (model is null || changingSelection) return;
        try { FlushInspector(); }
        catch (ArgumentException error)
        {
            ErrorText.Text = error.Message; changingSelection = true; ToolPicker.SelectedItem = previousTool; changingSelection = false; return;
        }
        bool enteringBackground=Tool=="Background"&&previousTool!="Background";
        previousTool = Tool; previewZoom=1;ZoomValue.Text=DetailPreview.IsChecked==true?"100%":"Fit";LayerHandles.CancelDrag(); model.CommitGesture();
        if(enteringBackground)model.AddDefaultBackground();
        CropHandles.CancelDrag(); CropHandles.AspectRatio = null; RebuildInspector(); RefreshPreview();
    }
    private void RebuildInspector()
    {
        Inspector.Children.Clear(); inspector = null; layerInspector=null;
        Heading.Text=Tool switch {"Adjust"=>"Edit Photo","Crop"=>"Crop Image","Background"=>"Add BG","Manual masks"=>"Redact Image","Annotate"=>"Annotate Image",_=>Tool};
        Title=Heading.Text+" · Citrus";
        ExportButton.Content=Tool switch{"Background"=>"Save with Background","Compress"=>"Compress","Crop" or "Resize" or "Rotate"=>"Apply",_=>"Save"};
        UpdateExportAvailability();
        bool layers=Tool is "Annotate" or "Manual masks";
        PreviewTools.Visibility=Tool is "Adjust" or "Annotate" or "Manual masks"?Visibility.Visible:Visibility.Collapsed;
        OriginalPreview.Visibility=Tool=="Adjust"?Visibility.Visible:Visibility.Collapsed;
        NativePreviewButton.Visibility=Tool=="Adjust"?Visibility.Visible:Visibility.Collapsed;
        if(Tool!="Adjust")OriginalPreview.IsChecked=false;
        LayerHandles.Visibility=layers&&model?.Selected?.Draft is not null?Visibility.Visible:Visibility.Collapsed;
        LayerHandles.Update(model?.Selected?.Draft,model?.Selected?.Size??default);
        UpdateTextMetrics();
        if(layers){DetailPreview.IsChecked=false;ZoomValue.Text="Fit";}
        if(!layers){LayerHandles.ResetView();PreviewImage.RenderTransform=Transform.Identity;}
        CropHandles.Visibility = Tool == "Crop" && model?.Selected?.Draft is not null ? Visibility.Visible : Visibility.Collapsed;
        DetailPreview.IsEnabled = Tool != "Crop" && !layers;
        if (model?.Selected?.Draft is null) { Inspector.Children.Add(new TextBlock { Text = model?.Selected?.Error ?? "Select an image to edit.", TextWrapping = TextWrapping.Wrap }); return; }
        if(layers)
        {
            if(Tool=="Manual masks"&&LayerHandles.Tool is not (ImageLayerTool.Select or ImageLayerTool.Solid or ImageLayerTool.Gaussian or ImageLayerTool.Pixelate))LayerHandles.Tool=ImageLayerTool.Solid;
            if(Tool=="Annotate"&&LayerHandles.Tool is ImageLayerTool.Solid or ImageLayerTool.Gaussian or ImageLayerTool.Pixelate)LayerHandles.Tool=ImageLayerTool.Select;
            layerInspector=new(model,LayerHandles,services,fontCatalog,this,Tool=="Manual masks",RefreshPreview,RebuildInspector,message=>{ErrorText.Text=message;if(message.Length>0)InspectorScroll.ScrollToTop();},lifetime.Token);
            Inspector.Children.Add(layerInspector);return;
        }
        inspector = new(model, Tool, RefreshPreview, message => ErrorText.Text = message, value => CropHandles.AspectRatio = value, RebuildInspector, CropHandles.AspectRatio,lifetime.Token);
        Inspector.Children.Add(inspector);

    }
    private RectD? GetTextBounds(Annotation a)=>model?.Selected?.Draft is {} doc?textMetrics.GetBounds(a,doc.Fonts):null;
    private async void UpdateTextMetrics()
    {
        if(closed||LayerHandles.IsDragging)return;
        long revision=++textMetricsGeneration;
        if(model?.Selected?.Draft is not {Recipe:ImageRecipe} document)return;
        try
        {
            await textMetrics.UpdateAsync(document,lifetime.Token);
            if(closed||revision!=textMetricsGeneration||model.Selected?.Draft!=document)return;
            LayerHandles.InvalidateVisual();layerInspector?.SyncGeometry();
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception error)when(error is not OutOfMemoryException){if(!closed&&revision==textMetricsGeneration)ErrorText.Text="Resolve font: "+error.Message;}
    }
    private void FlushInspector(){inspector?.FlushFields();layerInspector?.FlushFields();}
    private void LayerChanged(EditorDocument document,bool commit)
    {
        try
        {
            if(model?.Selected?.Draft is null)return;
            model.Change(document,true);if(commit)model.CommitGesture();
            LayerHandles.Update(model.Selected.Draft,model.Selected.Size);UpdateTextMetrics();layerInspector?.SyncGeometry();RefreshPreview();ErrorText.Text="";
        }
        catch(ArgumentException error){ErrorText.Text=error.Message;}
    }
    private void FitPreview(object sender,RoutedEventArgs e){DetailPreview.IsChecked=false;previewZoom=1;ZoomValue.Text="Fit";LayerHandles.ResetView();PreviewImage.RenderTransform=Transform.Identity;RefreshPreview();}
    private void NativePreview(object sender,RoutedEventArgs e){if(Tool!="Adjust")return;DetailPreview.IsChecked=true;previewZoom=1;ZoomValue.Text="100%";PreviewImage.RenderTransform=Transform.Identity;RefreshPreview();}
    private void ZoomIn(object sender,RoutedEventArgs e)=>SetPreviewZoom(Math.Min(8,previewZoom*1.25));
    private void ZoomOut(object sender,RoutedEventArgs e)=>SetPreviewZoom(Math.Max(DetailPreview.IsChecked==true?.125:1,previewZoom/1.25));
    private void SetPreviewZoom(double value){previewZoom=value;ZoomValue.Text=DetailPreview.IsChecked==true?$"{value*100:0}%":value==1?"Fit":$"Fit ×{value:0.##}";if(Tool is "Annotate" or "Manual masks")LayerHandles.SetZoom(value);else PreviewImage.RenderTransform=new ScaleTransform(value,value);}
    private void OriginalChanged(object sender,RoutedEventArgs e)=>RefreshPreview();
    private void CropChanged(RectD rect, bool commit)
    {
        try
        {
            if (model?.Selected?.Draft is not { Recipe: ImageRecipe recipe } doc) return;
            model.Change(doc with { Recipe = recipe with { Geometry = recipe.Geometry with { Crop = rect } } }, transient: true);
            if (commit) { model.CommitGesture(); RebuildInspector(); }
            RefreshPreview(); ErrorText.Text = "";
        }
        catch (ArgumentException error) { ErrorText.Text = error.Message; }
    }
    private async void RefreshPreview()
    {
        if (closed) return;
        previewWork?.Cancel(); long current = ++generation;
        if (model?.Selected is not { Draft: { Recipe: ImageRecipe recipe } document } row)
        {
            PreviewImage.Source = null; PreviewStatus.Text = model?.Selected?.Error ?? "Select an image to preview."; return;
        }
        var work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); previewWork = work;
        var previewDocument = document;
        if (Tool == "Crop")
        {
            var size = recipe.Geometry.QuarterTurns % 2 == 0 ? row.Size : new SizeI(row.Size.Height,row.Size.Width);
            CropHandles.Plane = size; CropHandles.Crop = recipe.Geometry.Crop ?? new(0,0,size.Width,size.Height); CropHandles.InvalidateVisual();
            // Crop context is the whole G plane; the overlay records the real crop in the draft.
            previewDocument = ImageCropContext.Create(document,row.Size);
        }
        if(OriginalPreview.IsChecked==true)
            previewDocument=document with{Sources=[..document.Sources.Where(s=>s.Id==recipe.SourceId)],Fonts=[],Recipe=new ImageRecipe(recipe.SourceId,new(0,false,false,null,null,false),new(1,0,0,0,1,0,0,1,0,0,0,0,0,0,0,0,0,0,0,1,PhotoEffect.None,0),[],null,[],[])};
        var monitorDpi=VisualTreeHelper.GetDpi(PreviewArea);
        var request = DetailPreview.IsChecked == true && Tool != "Crop"
            ? ImageDetailPreview.Create((ImageRecipe)previewDocument.Recipe,row.Size,current,ImagePreviewViewport.NativeBounds(PreviewArea.ActualWidth,PreviewArea.ActualHeight,monitorDpi.DpiScaleX,monitorDpi.DpiScaleY))
            : new PreviewRequest(current,new(512,512),null,null,null);
        RectD? tile = request.SourceTile;
        // Transient status text would resize the detail viewport and request another preview.
        session.Update(previewDocument);
        try
        {
            await Task.Delay(120, work.Token);
            var frame = await session.PreviewAsync(request, work.Token);
            if (frame is null) return;
            var bitmap = await Task.Run(() =>
            {
                var dpi=ImagePreviewViewport.BitmapDpi(tile is not null,monitorDpi.DpiScaleX,monitorDpi.DpiScaleY);
                var result = BitmapSource.Create(frame.Size.Width,frame.Size.Height,dpi.X,dpi.Y,PixelFormats.Bgra32,null,frame.Bgra32.ToArray(),frame.Stride);
                result.Freeze(); return result;
            }, work.Token);
            if (closed || current != generation || work.IsCancellationRequested) return;
            PreviewImage.Stretch = tile is not null ? Stretch.None : Stretch.Uniform;
            PreviewImage.Source = bitmap;
            PreviewStatus.Text = string.Join(" ",frame.Warnings.Where(warning => !warning.StartsWith("Scaled preview", StringComparison.OrdinalIgnoreCase)));
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!closed && current == generation) { PreviewImage.Source = null; PreviewStatus.Text = "Preview unavailable. " + error.Message; if(Tool is "Annotate" or "Manual masks")ErrorText.Text=error.Message; } }
        finally { if (ReferenceEquals(previewWork, work)) previewWork = null; work.Dispose(); }
    }
    private void DetailChanged(object sender, RoutedEventArgs e){previewZoom=1;PreviewImage.RenderTransform=Transform.Identity;ZoomValue.Text=DetailPreview.IsChecked==true?"100%":"Fit";RefreshPreview();}
    private void Undo(object sender, RoutedEventArgs e) { model?.Undo(); RebuildInspector(); RefreshPreview(); }
    private void Redo(object sender, RoutedEventArgs e) { model?.Redo(); RebuildInspector(); RefreshPreview(); }
    private void Reset(object sender, RoutedEventArgs e) { if(model?.Selected?.Draft is {} doc&&Tool=="Background"){var recipe=(ImageRecipe)doc.Recipe;model.Change(ImageCanvasPlacement.Change(doc,ImageGeometryMapper.Resolve(recipe,model.Selected.Size).OutputSize,null));}else model?.Reset(); RebuildInspector(); RefreshPreview(); }
    private void ApplyAll(object sender, RoutedEventArgs e)
    {
        try { FlushInspector(); model!.ApplyToAll(MappingMode.SelectedIndex == 1 ? ImageBatchGeometry.Pixels : ImageBatchGeometry.Relative, model.Selected?.ResizeIntent); ErrorText.Text = "";UpdateExportAvailability(); }
        catch (ArgumentException error) { ErrorText.Text = error.Message; }
    }
    private async void AddImages(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var picker = new OpenFileDialog { Title = "Add independent images", Multiselect = true, Filter = "Raster images|*.png;*.jpg;*.jpeg;*.webp;*.tif;*.tiff;*.bmp;*.gif;*.avif|All files|*.*" };
        if (picker.ShowDialog(this) == true) await Capture([.. picker.FileNames], true);
    }
    private async void ReloadImage(object sender, RoutedEventArgs e)
    {
        if (loading || model?.Selected is not { } row) return;
        if (row.Dirty && MessageBox.Show(this,"Reload this original and discard its draft edits?","Reload image",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No) != MessageBoxResult.Yes) return;
        loading = true; ExportButton.IsEnabled = false; previewWork?.Cancel(); PreviewImage.Source = null;
        try { await model.ReloadAsync(row,lifetime.Token); RebuildInspector(); RefreshPreview(); UpdateSummary(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!closed) ErrorText.Text = error.Message; }
        finally { loading = false; if (!closed) UpdateExportAvailability(); }
    }
    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Save image copies to" };
        if (picker.ShowDialog(this) == true) { model!.Destination = picker.FolderName; UpdateSummary(); }
    }
    private async void Export(object sender, RoutedEventArgs e)
    {
        if (loading || accepted is not null) return;
        ExportButton.IsEnabled=false;
        try
        {
            if(Tool=="Background"&&!BackgroundsReady)throw new ArgumentException("Add a background to every included image before saving, or exclude images without one.");
            FlushInspector(); model!.CommitGesture();var drafts=model.Sources.Select(r=>(Row:r,r.Included,r.Draft)).ToArray();
            await model.ValidateLayerDependenciesAsync(lifetime.Token);if(closed)return;
            if(drafts.Length!=model.Sources.Count||drafts.Any(d=>d.Included!=d.Row.Included||d.Draft!=d.Row.Draft))throw new ArgumentException("The draft changed during validation. Create copies again to use your latest edits.");
            accepted=model.Accept();DialogResult=true;
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch (ArgumentException error) { ErrorText.Text = error.Message; InspectorScroll.ScrollToTop(); }
        finally{if(!closed)UpdateExportAvailability();}
    }
    private void Cancel(object sender, RoutedEventArgs e) => Close();
    private void CheckClose(object? sender, CancelEventArgs e)
    {
        if (accepted is not null || lifetime.IsCancellationRequested || model?.Dirty != true) return;
        e.Cancel = MessageBox.Show(this,"Discard these edits?","Keep editing?",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No) != MessageBoxResult.Yes;
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (CropHandles.IsDragging || LayerHandles.IsDragging || inspector?.GestureActive == true) { CropHandles.CancelDrag(); LayerHandles.CancelDrag(); model?.CancelGesture(); RebuildInspector(); RefreshPreview(); }
            else Close();
            e.Handled = true;
        }
        // Text editing keeps its native undo/redo and Enter never triggers export.
        if (Keyboard.FocusedElement is TextBoxBase) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.Z or Key.Y)
        { if (e.Key == Key.Z) Undo(this,e); else Redo(this,e); e.Handled = true; }
    }
}
