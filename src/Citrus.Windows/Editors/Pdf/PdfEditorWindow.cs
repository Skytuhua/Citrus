using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Citrus.Conversion;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Presentation;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using ComboBox=System.Windows.Controls.ComboBox;
using ListBox=System.Windows.Controls.ListBox;
using CheckBox=System.Windows.Controls.CheckBox;
using ImageView=System.Windows.Controls.Image;

namespace Citrus.Windows.Editors.Pdf;

/// <summary>A preview-first PDF editor. The accepted result contains source identities and page recipes, never preview pixels.</summary>
public sealed class PdfEditorWindow : Window
{
    private readonly PdfEngine engine;
    private readonly CancellationTokenSource lifetime=new();
    private readonly ObservableCollection<PageItem> visiblePages=[];
    private readonly Dictionary<Guid,List<PageItem>> perSource=[];
    private readonly List<SourceSnapshot> sources=[];
    private readonly Dictionary<Guid,PdfSourceInfo> information=[];
    private readonly Dictionary<string,TextBox> metadata=[];
    private readonly Dictionary<string,Button> metadataRemovalButtons=[];
    private readonly Dictionary<(Guid Source,string Key),string> metadataBeforeRemoval=[];
    private readonly HashSet<string> removed=[];
    private readonly Dictionary<Guid,ImmutableArray<MetadataEdit>> metadataDrafts=[];
    private readonly Dictionary<Guid,MetadataPolicy> metadataPolicies=[];
    private readonly Stack<ImmutableArray<PdfPageRef>> undo=[],redo=[];
    private readonly ListBox pages=new() { SelectionMode=SelectionMode.Extended,MinWidth=170 };
    private readonly ImageView preview=new() { Stretch=Stretch.Uniform,Margin=new(16) };
    private readonly TextBlock status=new() { Text="Preparing PDF…",TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock error=new() { TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0) };
    private TextBlock? technicalProperties;
    private readonly StackPanel controls=new() { Margin=new(0,16,0,0) };
    private readonly Grid body=new() { Margin=new(24,16,24,16) };
    private readonly Grid canvas=new() { Margin=new(0,16,0,0) };
    private readonly StackPanel below=new();
    private readonly ComboBox sourcePicker=new() { MinWidth=160,MaxWidth=380 };
    private readonly ComboBox compressionMode=new() { ItemsSource=new[] { "Percentage","Target size","Quality" },SelectedIndex=0,MinWidth=150 };
    private readonly TextBox amount=new() { Text="50",MinWidth=110 };
    private readonly TextBox selection=new() { Text="",MinWidth=180 };
    private readonly Button apply=new() { Content="Apply",IsDefault=true,IsEnabled=false,MinWidth=92 };
    private readonly Button reset=new() { Content="Reset" };
    private readonly List<Button> editingButtons=[];
    private EditorOpenRequest? request;
    private EditorSubmissionBatch? accepted;
    private Guid selectedSource;
    private bool loaded,closing,dirty,discardRequested,synchronizingControls;
    private long previewGeneration;
    private Task loading=Task.CompletedTask;
    private readonly HashSet<Task> previews=[];
    private readonly string previewRoot=Path.Combine(Path.GetTempPath(),"citrus-pdf-view-"+Guid.NewGuid().ToString("N"));
    private IReadOnlyDictionary<Guid,string> staged=new Dictionary<Guid,string>();
    private bool thumbnailsRunning;
    private int firstVisiblePage;
    private long thumbnailGeneration;
    private CancellationTokenSource? previewWork;
    private readonly Queue<PageItem> thumbnailLru=[];
    private PdfOutputMode mode=PdfOutputMode.Document;
    private MetadataPolicy metadataPolicy=MetadataPolicy.PreserveSupported;
    private PdfCompressionMode pdfCompression=PdfCompressionMode.PreserveStructure;
    private CheckBox? rasterRebuild;

    public PdfEditorWindow(PdfEngine engine)
    {
        this.engine=engine; Width=780;Height=620;MinWidth=560;MinHeight=440;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var sourceName=new FrameworkElementFactory(typeof(TextBlock));
        sourceName.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(SourceSnapshot.Path)) { Converter=new SourceNameConverter() });
        sourceName.SetBinding(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding(nameof(SourceSnapshot.Path)));
        sourcePicker.ItemTemplate=new DataTemplate(typeof(SourceSnapshot)) { VisualTree=sourceName };
        AutomationProperties.SetName(sourcePicker,"PDF file");
        SetResourceReference(FontFamilyProperty,"AppFont"); SetResourceReference(FontSizeProperty,"BodySize"); SetResourceReference(ForegroundProperty,"PrimaryText");
        var surface=new MaterialSurface(); var grid=new Grid(); grid.RowDefinitions.Add(new() { Height=GridLength.Auto });grid.RowDefinitions.Add(new());grid.RowDefinitions.Add(new() { Height=GridLength.Auto });surface.Child=grid;Content=surface;
        var header=new ChromeHeader { Content=new TextBlock { Text="PDF",FontSize=22,FontWeight=FontWeights.SemiBold },MinHeight=56 };grid.Children.Add(header);
        body.RowDefinitions.Add(new() { Height=GridLength.Auto });body.RowDefinitions.Add(new());body.RowDefinitions.Add(new() { Height=GridLength.Auto });grid.Children.Add(body);Grid.SetRow(body,1);
        var sourceRow=new DockPanel();sourceRow.Children.Add(sourcePicker);sourceRow.Children.Add(status);body.Children.Add(sourceRow);DockPanel.SetDock(sourcePicker,Dock.Left);status.Margin=new(12,0,0,0);
        canvas.ColumnDefinitions.Add(new() { Width=new(200) });canvas.ColumnDefinitions.Add(new());body.Children.Add(canvas);Grid.SetRow(canvas,1);
        pages.ItemsSource=visiblePages;pages.ItemTemplate=PageTemplate();pages.SetResourceReference(BackgroundProperty,"PanelBackground");canvas.Children.Add(pages);
        var previewBorder=new Border { Background=new SolidColorBrush(Color.FromRgb(238,240,241)),CornerRadius=new(12),Margin=new(16,0,0,0),Child=preview,ClipToBounds=true };canvas.Children.Add(previewBorder);Grid.SetColumn(previewBorder,1);
        below.Children.Add(controls);below.Children.Add(error);body.Children.Add(below);Grid.SetRow(below,2);
        var footerBorder=new Border { BorderThickness=new(0,1,0,0) };footerBorder.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");grid.Children.Add(footerBorder);Grid.SetRow(footerBorder,2);
        var footer=new Grid { Margin=new(24,12,24,18) };footer.ColumnDefinitions.Add(new());footer.ColumnDefinitions.Add(new() { Width=GridLength.Auto });footerBorder.Child=footer;
        var secondary=new StackPanel { Orientation=Orientation.Horizontal };secondary.Children.Add(reset);var cancel=new Button { Content="Cancel",IsCancel=true,Margin=new(8,0,0,0) };cancel.Click+=(_,_)=>Close();secondary.Children.Add(cancel);footer.Children.Add(secondary);footer.Children.Add(apply);Grid.SetColumn(apply,1);
        reset.SetResourceReference(StyleProperty,"SecondaryButton");cancel.SetResourceReference(StyleProperty,"SecondaryButton");
        apply.SetResourceReference(StyleProperty,"PrimaryButton");
        apply.Click+=(_,_)=>Apply();reset.Click+=(_,_)=>Reset();sourcePicker.SelectionChanged+=(_,_)=>SelectSource();pages.SelectionChanged+=(_,_)=>QueuePreview();
        pages.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,e)=>{firstVisiblePage=Math.Max(0,(int)e.VerticalOffset);thumbnailGeneration++;QueueThumbnails();}));
        PreviewKeyDown+=(_,e)=>
        {
            if(e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)return;
            if(e.Key==Key.Z && Keyboard.Modifiers==ModifierKeys.Control) { Undo();e.Handled=true; }
            else if(e.Key==Key.Y && Keyboard.Modifiers==ModifierKeys.Control) { Redo();e.Handled=true; }
            else if(e.Key==Key.Delete && pages.IsKeyboardFocusWithin) { RemovePages();e.Handled=true; }
        };
        Closing+=(_,e)=>
        {
            if(dirty && accepted is null && !discardRequested && !lifetime.IsCancellationRequested)
            { e.Cancel=true;discardRequested=true;error.Text="Close again to discard unsaved changes.";return; }
            closing=true;lifetime.Cancel();
        };
        WindowChromeSetup.Apply(this);(Application.Current as App)?.AttachPresentation(this,surface);
    }
    public async Task<EditorSubmissionBatch?> ShowEditorAsync(EditorOpenRequest value,CancellationToken ct)
    {
        if(loaded) throw new InvalidOperationException("An editor can be opened once.");loaded=true;request=value;
        if(value.EntryTool is "Metadata" or "Compress") {Width=600;Height=560;}
        if(!engine.Routes.TryGet(value.Operation.Id,out _)) throw new NotSupportedException("Unsupported PDF editor entry.");
        Title=value.EntryTool+" PDF";((TextBlock)((ChromeHeader)((Grid)((MaterialSurface)Content).Child).Children[0]).Content).Text=Title;
        apply.Content=value.EntryTool switch { "Compress"=>"Compress","Metadata"=>"Save",_=>"Apply" };
        Loaded+=(_,_)=>loading=LoadAsync();
        using var registration=ct.Register(()=>Dispatcher.BeginInvoke(new Action(()=> { if(!closing) { discardRequested=true;Close(); } })));
        try { ct.ThrowIfCancellationRequested();ShowDialog();return accepted; }
        finally { lifetime.Cancel();await loading;await Task.WhenAll(previews.ToArray());if(Directory.Exists(previewRoot)) Directory.Delete(previewRoot,true);lifetime.Dispose(); }
    }
    private async Task LoadAsync()
    {
        try
        {
            var captured=await engine.Sources.CaptureAsync(request!.InitialPaths,lifetime.Token);
            if(captured.Any(c=>c.Snapshot is null)) throw new IOException(string.Join("; ",captured.Where(c=>c.Snapshot is null).Select(c=>c.Error)));
            sources.AddRange(captured.Select(c=>c.Snapshot!));
            staged=await engine.Sources.StageVerifiedAsync([..sources],previewRoot,lifetime.Token);
            foreach(var capture in captured)
            {
                if(capture.Snapshot is null) throw new IOException(capture.Error);
                var source=capture.Snapshot;var info=await engine.InspectAsync(staged[source.Id],lifetime.Token);information.Add(source.Id,info);
                perSource.Add(source.Id,Enumerable.Range(1,info.PageCount).Select(n=>new PageItem(new(Guid.NewGuid(),source.Id,n,0),Path.GetFileName(source.Path))).ToList());
            }
            sourcePicker.ItemsSource=sources;sourcePicker.SelectedIndex=0;sourcePicker.Visibility=request.Operation.IsGroup?Visibility.Collapsed:Visibility.Visible;
            selectedSource=sources[0].Id;synchronizingControls=true;try { ConfigureControls(); } finally { synchronizingControls=false; }RefreshPages();apply.IsEnabled=true;status.Text=$"{sources.Count} file(s) · {sources.Sum(s=>information[s.Id].PageCount)} pages";
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch(Exception failure) when(failure is not OutOfMemoryException) { error.Text=failure.Message;status.Text="PDF could not be opened"; }
    }
    private void ConfigureControls()
    {
        string tool=request!.EntryTool;
        if(tool is "Metadata" or "Compress")
        {
            canvas.Visibility=Visibility.Collapsed;
            body.Children.Remove(below);
            var scroll=new ScrollViewer {Content=below,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            body.Children.Add(scroll);Grid.SetRow(scroll,1);
        }
        if(tool=="Metadata")
        {
            controls.Children.Add(new TextBlock { Text="Search metadata",Margin=new(0,0,0,4) });var search=new TextBox { Margin=new(0,0,0,8) };AutomationProperties.SetName(search,"Search metadata");controls.Children.Add(search);
            var rows=new StackPanel();controls.Children.Add(rows);
            foreach(var field in information[selectedSource].Metadata)
            {
                var row=new DockPanel { Margin=new(0,4,0,4),Tag=field.Key };var label=new TextBlock { Text=field.DisplayName,Width=100,VerticalAlignment=VerticalAlignment.Center };row.Children.Add(label);
                var remove=new Button { Content="−",Width=32,Margin=new(8,0,0,0),IsEnabled=field.Removable };remove.SetResourceReference(StyleProperty,"TertiaryButton");AutomationProperties.SetName(remove,"Remove "+field.DisplayName);DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);
                var input=new TextBox { Text=((TextValue)field.Value).Value,IsReadOnly=!field.Writable };metadata[field.Key]=input;metadataRemovalButtons[field.Key]=remove;
                AutomationProperties.SetName(input,field.DisplayName);
                input.TextChanged+=(_,_)=> { if(synchronizingControls)return;removed.Remove(field.Key);if(metadataPolicy==MetadataPolicy.StripAll)metadataPolicy=MetadataPolicy.StripDescriptive;RefreshMetadataRemoval();MarkDirty(); };
                remove.Click+=(_,_)=>
                {
                    synchronizingControls=true;
                    try
                    {
                        var identity=(selectedSource,field.Key);
                        if(removed.Remove(field.Key))
                        {
                            string original=((TextValue)information[selectedSource].Metadata.Single(item=>item.Key==field.Key).Value).Value;
                            input.Text=metadataBeforeRemoval.GetValueOrDefault(identity,original);
                            if(metadataPolicy==MetadataPolicy.StripAll)metadataPolicy=MetadataPolicy.StripDescriptive;
                        }
                        else {metadataBeforeRemoval[identity]=input.Text;removed.Add(field.Key);input.Text="";}
                        RefreshMetadataRemoval();
                    }
                    finally {synchronizingControls=false;}
                    MarkDirty();
                };
                row.Children.Add(input);rows.Children.Add(row);
            }
            technicalProperties=new TextBlock { Margin=new(0,16,0,4),TextWrapping=TextWrapping.Wrap };RefreshTechnicalProperties();rows.Children.Add(technicalProperties);
            search.TextChanged+=(_,_)=> { foreach(FrameworkElement row in rows.Children) if(row.Tag is string label)row.Visibility=label.Contains(search.Text,StringComparison.OrdinalIgnoreCase)||metadata[label].Text.Contains(search.Text,StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed; };
            var removeAll=new Button { Content="Remove All",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,8,0,0) };removeAll.SetResourceReference(StyleProperty,"SecondaryButton");removeAll.Click+=(_,_)=>
            {
                synchronizingControls=true;
                try {metadataPolicy=MetadataPolicy.StripAll;foreach(var field in metadata) {if(!removed.Contains(field.Key))metadataBeforeRemoval[(selectedSource,field.Key)]=field.Value.Text;field.Value.Text="";removed.Add(field.Key);}RefreshMetadataRemoval();}
                finally {synchronizingControls=false;}
                MarkDirty();
            };removeAll.ToolTip="Remove descriptive metadata";controls.Children.Add(removeAll);
            return;
        }
        if(tool=="Compress")
        {
            var row=new DockPanel();row.Children.Add(new TextBlock { Text="Mode",Width=100,VerticalAlignment=VerticalAlignment.Center });row.Children.Add(compressionMode);row.Children.Add(amount);controls.Children.Add(row);
            var slider=new System.Windows.Controls.Slider { Minimum=1,Maximum=99,Value=50,Margin=new(0,16,0,8),TickFrequency=1,IsSnapToTickEnabled=true };controls.Children.Add(slider);bool synchronizing=false;
            var explanation=new TextBlock { Margin=new(0,8,0,0),TextWrapping=TextWrapping.Wrap };controls.Children.Add(explanation);
            void Explain()
            {
                long original=sources.First(s=>s.Id==selectedSource).Length;
                string target=compressionMode.SelectedIndex switch
                {
                    0 when decimal.TryParse(amount.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out var entered)&&entered>0&&entered<100=>$"Maximum: {decimal.Floor(original*(1-entered/100)):N0} bytes. No output if the target cannot be met.",
                    0=>"Choose a reduction between 0 and 100 percent.",
                    1=>"Target in KB. No output if the target cannot be met.",
                    _=>"Enable page image rebuilding to use quality."
                };
                explanation.Text=$"Original size: {original:N0} bytes. {target}";MarkDirty();
            }
            compressionMode.SelectionChanged+=(_,_)=> { slider.Visibility=compressionMode.SelectedIndex==1?Visibility.Collapsed:Visibility.Visible;slider.Maximum=compressionMode.SelectedIndex==2?100:99;Explain(); };
            slider.ValueChanged+=(_,_)=> { if(!synchronizing){synchronizing=true;amount.Text=slider.Value.ToString("0",CultureInfo.InvariantCulture);synchronizing=false;}Explain(); };
            amount.TextChanged+=(_,_)=> { if(!synchronizing&&compressionMode.SelectedIndex!=1&&double.TryParse(amount.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out double value)&&double.IsFinite(value)){synchronizing=true;slider.Value=Math.Clamp(value,slider.Minimum,slider.Maximum);synchronizing=false;}Explain(); };Explain();
            rasterRebuild=new CheckBox { Content=new TextBlock { Text="Rebuild as page images (removes editable text, forms, links and accessibility)",TextWrapping=TextWrapping.Wrap },Margin=new(0,8,0,0) };rasterRebuild.Checked+=(_,_)=> { pdfCompression=PdfCompressionMode.RasterRebuild;MarkDirty(); };rasterRebuild.Unchecked+=(_,_)=> { pdfCompression=PdfCompressionMode.PreserveStructure;MarkDirty(); };controls.Children.Add(rasterRebuild);return;
        }
        var toolbar=new WrapPanel();controls.Children.Add(toolbar);
        Add("Move up",()=>Move(-1));Add("Move down",()=>Move(1));Add("Rotate",Rotate);Add("Duplicate",Duplicate);Add("Remove",RemovePages);Add("Undo",Undo);Add("Redo",Redo);
        if(tool=="Split") { mode=PdfOutputMode.SeparatePageDocuments;controls.Children.Add(new TextBlock { Text="Pages to split (blank = all retained)",Margin=new(0,12,0,4) });controls.Children.Add(selection); }
        if(request.Operation.Id=="pdf.images") mode=PdfOutputMode.PageImages;
        if(request.Operation.Id=="pdf.text") mode=PdfOutputMode.Text;
        void Add(string label,Action action) { var button=new Button { Content=label,Margin=new(0,0,8,8) };button.SetResourceReference(StyleProperty,"SecondaryButton");button.Click+=(_,_)=>action();toolbar.Children.Add(button);editingButtons.Add(button); }
    }
    private List<PageItem> Current()=>request!.Operation.IsGroup?perSource.Values.SelectMany(p=>p).ToList():perSource[selectedSource].ToList();
    private void SelectSource()
    {
        if(sourcePicker.SelectedItem is not SourceSnapshot source) return;
        SaveMetadataDraft();selectedSource=source.Id;RefreshTechnicalProperties();undo.Clear();redo.Clear();RefreshPages();
        if(metadata.Count>0)
        {
            synchronizingControls=true;try
            {
            removed.Clear();metadataPolicy=metadataPolicies.GetValueOrDefault(selectedSource,MetadataPolicy.PreserveSupported);
            foreach(var field in information[selectedSource].Metadata)
            {
                var edit=metadataDrafts.GetValueOrDefault(selectedSource,[]).FirstOrDefault(e=>e.Key==field.Key);
                metadata[field.Key].Text=edit?.Action==MetadataEditAction.Remove?"":edit?.Value is TextValue value?value.Value:((TextValue)field.Value).Value;
                if(edit?.Action==MetadataEditAction.Remove)removed.Add(field.Key);
            }
            RefreshMetadataRemoval();
            } finally { synchronizingControls=false; }
        }
    }
    private void SaveMetadataDraft()
    {
        if(metadata.Count==0||selectedSource==Guid.Empty)return;
        metadataDrafts[selectedSource]=[..metadata.Select(pair=>new MetadataEdit("PDF",pair.Key,removed.Contains(pair.Key)?MetadataEditAction.Remove:MetadataEditAction.Set,removed.Contains(pair.Key)?null:new TextValue(pair.Value.Text)))];metadataPolicies[selectedSource]=metadataPolicy;
    }
    private void RefreshTechnicalProperties()
    {
        if(technicalProperties is null||!information.TryGetValue(selectedSource,out var info))return;
        technicalProperties.Text=$"Pages: {info.PageCount:N0}\nSize: {sources.First(s=>s.Id==selectedSource).Length:N0} bytes";
    }
    private void RefreshMetadataRemoval()
    {
        foreach(var (key,input) in metadata)
        {
            bool isRemoved=removed.Contains(key);input.Opacity=isRemoved?.55:1;
            if(metadataRemovalButtons.TryGetValue(key,out var button))
            {button.Content=isRemoved?"↶":"−";button.ToolTip=isRemoved?"Restore field":"Remove field";AutomationProperties.SetName(button,(isRemoved?"Restore ":"Remove ")+key);}
        }
    }
    private void RefreshPages()
    { thumbnailGeneration++;firstVisiblePage=0;visiblePages.Clear();foreach(var page in Current()) visiblePages.Add(page);if(visiblePages.Count>0) pages.SelectedIndex=0;QueueThumbnails(); }
    private void Commit(IEnumerable<PageItem> next)
    {
        undo.Push([..Current().Select(p=>p.Page)]);redo.Clear();Install(next);MarkDirty();
    }
    private void Install(IEnumerable<PageItem> next)
    {
        var list=next.ToList();if(request!.Operation.IsGroup) { foreach(var source in sources) perSource[source.Id]=[];perSource[sources[0].Id]=list; }
        else perSource[selectedSource]=list;
        RefreshPages();
    }
    private void Move(int delta)
    {
        var selected=pages.SelectedItems.Cast<PageItem>().ToHashSet();var next=Current();var indexes=Enumerable.Range(0,next.Count).Where(i=>selected.Contains(next[i]));if(delta>0) indexes=indexes.Reverse();
        bool changed=false;foreach(int i in indexes) { int target=i+delta;if(target<0||target>=next.Count||selected.Contains(next[target])) continue;(next[i],next[target])=(next[target],next[i]);changed=true; }if(changed) Commit(next);
    }
    private void Rotate() { var selected=pages.SelectedItems.Cast<PageItem>().ToHashSet();Commit(Current().Select(p=>selected.Contains(p)?new(p.Page with { QuarterTurns=(p.Page.QuarterTurns+1)%4 },p.SourceName):p)); }
    private void Duplicate() { var selected=pages.SelectedItems.Cast<PageItem>().ToHashSet();Commit(Current().SelectMany(p=>selected.Contains(p)?new[] { p,new PageItem(p.Page with { Id=Guid.NewGuid() },p.SourceName) }:new[] { p })); }
    private void RemovePages() { var selected=pages.SelectedItems.Cast<PageItem>().ToHashSet();if(selected.Count>0) Commit(Current().Where(p=>!selected.Contains(p))); }
    private void Undo() { if(undo.TryPop(out var state)) { redo.Push([..Current().Select(p=>p.Page)]);Install(state.Select(p=>new PageItem(p,Path.GetFileName(sources.First(s=>s.Id==p.SourceId).Path))));MarkDirty(); } }
    private void Redo() { if(redo.TryPop(out var state)) { undo.Push([..Current().Select(p=>p.Page)]);Install(state.Select(p=>new PageItem(p,Path.GetFileName(sources.First(s=>s.Id==p.SourceId).Path))));MarkDirty(); } }
    private void Reset()
    {
        if(sources.Count==0)return;
        synchronizingControls=true;
        try
        {
            foreach(var source in sources)perSource[source.Id]=Enumerable.Range(1,information[source.Id].PageCount).Select(n=>new PageItem(new(Guid.NewGuid(),source.Id,n,0),Path.GetFileName(source.Path))).ToList();
            removed.Clear();metadataDrafts.Clear();metadataPolicies.Clear();metadataBeforeRemoval.Clear();metadataPolicy=MetadataPolicy.PreserveSupported;
            foreach(var field in information[selectedSource].Metadata)if(metadata.TryGetValue(field.Key,out var input))input.Text=((TextValue)field.Value).Value;
            RefreshMetadataRemoval();
            if(request?.EntryTool=="Compress") {compressionMode.SelectedIndex=0;amount.Text="50";pdfCompression=PdfCompressionMode.PreserveStructure;if(rasterRebuild is not null)rasterRebuild.IsChecked=false;}
            undo.Clear();redo.Clear();RefreshPages();
        }
        finally{synchronizingControls=false;}
        MarkDirty();
    }
    private void MarkDirty() { if(sources.Count==0||synchronizingControls) return;dirty=true;discardRequested=false; }
    private void QueuePreview()
    {
        if(closing || pages.SelectedItem is not PageItem item) return;previewWork?.Cancel();var work=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);previewWork=work;
        var task=ShowPreviewAsync(item,++previewGeneration,work.Token);previews.Add(task);_=ObservePreview(task,work);
    }
    private async Task ObservePreview(Task task,CancellationTokenSource work)
    { try { await task; } finally { previews.Remove(task);if(ReferenceEquals(previewWork,work))previewWork=null;work.Dispose(); } }
    private async Task Observe(Task task) { try { await task; } finally { previews.Remove(task); } }
    private async Task ShowPreviewAsync(PageItem item,long generation,CancellationToken ct)
    {
        try
        {
            byte[] png=await PdfEngine.RenderPageAsync(staged[item.Page.SourceId],item.Page.OneBasedPage,100,ct);
            if(closing||generation!=previewGeneration||ct.IsCancellationRequested)return;
            using var stream=new MemoryStream(png);
            var image=await PdfPreviewBitmap.DecodeAsync(stream,(Rotation)item.Page.QuarterTurns,0,0,ct);
            if(closing||generation!=previewGeneration||ct.IsCancellationRequested)return;
            preview.Source=image;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch(Exception failure) when(failure is not OutOfMemoryException) { if(!closing && generation==previewGeneration) error.Text=failure.Message; }
    }
    private void QueueThumbnails()
    { if(closing||thumbnailsRunning||staged.Count==0||visiblePages.Count==0)return;var task=LoadThumbnailsAsync();previews.Add(task);_=Observe(task); }
    private async Task LoadThumbnailsAsync()
    {
        thumbnailsRunning=true;
        long generation=thumbnailGeneration;
        try
        {
            int first=Math.Clamp(firstVisiblePage,0,Math.Max(0,visiblePages.Count-1));
            foreach(var item in visiblePages.Skip(first).Take(12).ToArray())
            {
                if(generation!=thumbnailGeneration)break;
                if(item.Thumbnail is not null)continue;
                byte[] png=await PdfEngine.RenderPageAsync(staged[item.Page.SourceId],item.Page.OneBasedPage,48,lifetime.Token);
                if(closing||generation!=thumbnailGeneration)return;
                int pixelWidth=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4)),pixelHeight=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4));
                bool wide=pixelWidth/(double)pixelHeight>1.4;
                using var stream=new MemoryStream(png);
                var image=await PdfPreviewBitmap.DecodeAsync(stream,(Rotation)item.Page.QuarterTurns,wide?Math.Min(140,pixelWidth):0,wide?0:Math.Min(100,pixelHeight),lifetime.Token);
                if(closing||generation!=thumbnailGeneration||lifetime.IsCancellationRequested)return;
                item.Thumbnail=image;thumbnailLru.Enqueue(item);
                while(thumbnailLru.Count>128)thumbnailLru.Dequeue().Thumbnail=null;
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested) { }
        catch(Exception failure)when(failure is not OutOfMemoryException) { if(!closing)error.Text=failure.Message; }
        finally { thumbnailsRunning=false;if(generation!=thumbnailGeneration&&!closing)QueueThumbnails(); }
    }
    private void Apply()
    {
        if(request is null || sources.Count==0) return;
        try
        {
            CompressionGoal goal=new QualityGoal(85);
            SaveMetadataDraft();
            if(request.EntryTool=="Compress")
            {
                if(!decimal.TryParse(amount.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out decimal value)) throw new ArgumentException("Enter a valid compression amount.");
                goal=compressionMode.SelectedIndex switch { 0 when value>0&&value<100=>new ReductionGoal(value),1 when value>0=>new ByteGoal(checked((long)(value*1024))),2 when value>=1&&value<=100=>new QualityGoal((int)value),_=>throw new ArgumentException("Percentage must be between 0 and 100; size must be positive; quality must be 1–100.") };
                if(goal is QualityGoal && pdfCompression!=PdfCompressionMode.RasterRebuild)
                    throw new ArgumentException("Enable page image rebuilding for Quality, or use Percentage or Target size to keep editable content.");
            }
            var items=ImmutableArray.CreateBuilder<EditorSubmissionItem>();var included=ImmutableArray.CreateBuilder<Guid>();
            var groups=request.Operation.IsGroup?new[] { Current() }:sources.Select(s=>perSource[s.Id]).ToArray();
            foreach(var group in groups)
            {
                var retained=group;
                if(request.EntryTool=="Split" && !string.IsNullOrWhiteSpace(selection.Text)) { var chosen=PdfPageSelection.Parse(selection.Text,group.Count);retained=chosen.Select(n=>group[n-1]).ToList(); }
                if(retained.Count==0) continue;var rootIds=sources.Where(s=>retained.Any(p=>p.Page.SourceId==s.Id)).Select(s=>s.Id).ToImmutableArray();included.AddRange(rootIds);
                string format=mode switch { PdfOutputMode.PageImages=>"png",PdfOutputMode.Text=>"txt",_=>"pdf" };
                var edits=metadataDrafts.GetValueOrDefault(rootIds[0],[]);var policy=metadataPolicies.GetValueOrDefault(rootIds[0],MetadataPolicy.PreserveSupported);
                var doc=new EditorDocument(1,0,[..sources.Where(s=>rootIds.Contains(s.Id))],[],new PdfRecipe([..retained.Select(p=>p.Page)],mode,150,pdfCompression,goal is QualityGoal quality?quality.Value:85,edits),new(format,request.DestinationDirectory,Path.GetFileNameWithoutExtension(sources.First(s=>rootIds.Contains(s.Id)).Path)+"-"+request.EntryTool.ToLowerInvariant(),goal,policy,new(255,255,255,255)));
                var issues=EditorRecipeValidator.Validate(doc);if(issues.Length>0) throw new ArgumentException(string.Join("; ",issues.Select(i=>i.Message)));
                items.Add(new(Guid.NewGuid(),rootIds,doc));
            }
            if(items.Count==0) throw new ArgumentException("Keep at least one page.");
            var includedIds=included.Distinct().ToImmutableArray();accepted=new(request,[..sources],[..sources.Where(s=>!includedIds.Contains(s.Id)).Select(s=>s.Path)],[],includedIds,items.ToImmutable());Close();
        }
        catch(Exception failure) when(failure is ArgumentException or OverflowException) { error.Text=failure.Message; }
    }
    private static DataTemplate PageTemplate()
    {
        var template=new DataTemplate(typeof(PageItem));var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(MarginProperty,new Thickness(8));var image=new FrameworkElementFactory(typeof(ImageView));image.SetValue(FrameworkElement.HeightProperty,100d);image.SetValue(ImageView.StretchProperty,Stretch.Uniform);image.SetBinding(ImageView.SourceProperty,new System.Windows.Data.Binding(nameof(PageItem.Thumbnail)));panel.AppendChild(image);var text=new FrameworkElementFactory(typeof(TextBlock));text.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(PageItem.Label)));text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);panel.AppendChild(text);template.VisualTree=panel;return template;
    }
    private sealed class SourceNameConverter:System.Windows.Data.IValueConverter
    {
        public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>value is string path?Path.GetFileName(path):"";
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>System.Windows.Data.Binding.DoNothing;
    }
    private sealed class PageItem(PdfPageRef page,string sourceName):INotifyPropertyChanged
    {
        public PdfPageRef Page {get;}=page;public string SourceName {get;}=sourceName;
        public string Label=>$"{SourceName}\nPage {Page.OneBasedPage} · {Page.QuarterTurns*90}°";
        private ImageSource? thumbnail;public ImageSource? Thumbnail { get=>thumbnail;set {thumbnail=value;PropertyChanged?.Invoke(this,new(nameof(Thumbnail)));} }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
