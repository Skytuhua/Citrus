using System.Collections.Immutable;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Citrus.Conversion.Editing.Media;
using Citrus.Core;
using Citrus.Core.Editing;
using Citrus.Windows.Presentation;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;
using Slider=System.Windows.Controls.Slider;

namespace Citrus.Windows.Editors.Media;

/// <summary>Tool-specific native media views sharing chrome, immutable recipes and actual processed playback.</summary>
public sealed class MediaEditorWindow : Window
{
    private readonly MediaEditorEngine engine;
    private readonly CancellationTokenSource lifetime=new();
    private readonly HashSet<Task> work=[];
    private readonly string previewRoot=Path.Combine(Path.GetTempPath(),"citrus-media-ui-"+Guid.NewGuid().ToString("N"));
    private readonly StackPanel body=new();
    private readonly ContentControl videoPane=new(){Visibility=Visibility.Collapsed,Margin=new(18,10,18,0)};
    private readonly ScrollViewer toolsScroll;
    private MediaProbe? sessionProbe;
    private VideoTrimSelectionView? videoTrim;
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button apply=new(){MinWidth=100,HorizontalAlignment=HorizontalAlignment.Right};
    private readonly TextBlock heading=new(){FontWeight=FontWeights.SemiBold};
    private readonly DispatcherTimer previewDelay=new(){Interval=TimeSpan.FromMilliseconds(120)};
    private readonly List<EditorDocument> documents=[];
    private readonly List<EditorDocument> initialDocuments=[];
    private readonly List<EditorHistory> histories=[];
    private readonly List<SourceSnapshot> sources=[];
    private readonly Dictionary<Guid,Guid?> selectedBleeps=[];
    private readonly Dictionary<Guid,double> playheads=[];
    private readonly Dictionary<Guid,bool> resizeAspect=[];
    private readonly HashSet<TextBox> invalidFields=[];
    private Action? commitResizeFields;
    private EditorOpenRequest request=null!;
    private EditorDocument document=null!;
    private MediaSourceInfo info=null!;
    private MediaPreviewController? playback;
    private MediaTransport? transport;
    private MediaRangeView? range;
    private readonly List<WaveformView> channelViews=[];
    private EditorSubmissionBatch? accepted;
    private ImmutableArray<MetadataField> metadata=[];
    private StackPanel? metadataRows;
    private TextBox? search,startField,endField;
    private TextBlock? durationText,bleepCount;
    private Slider? bleepFrequency,bleepGain;
    private ComboBox? bleepMode;
    private ComboBox? sourcePicker;
    private System.Windows.Controls.Image? visualizationPreview;
    private Slider? fullTimeline;
    private TextBlock? auditionReadout;
    private int selected;
    private bool closed,shown,hydratingControls,invalidVideoSize,applying;
    private CancellationTokenSource? previewWork;
    private long viewGeneration;
    private long sourceGeneration;
    private Task loading=Task.CompletedTask;
    public EditorDocument? CurrentDocument=>documents.Count==0?null:document;
    public bool IsReady {get;private set;}
    private string Id=>request.Operation.Id;
    public MediaEditorWindow(MediaEditorEngine engine)
    {
        this.engine=engine;
        NameScope.SetNameScope(this,new NameScope());
        Width=640;Height=540;MinWidth=440;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty,"AppFont");SetResourceReference(FontSizeProperty,"BodySize");SetResourceReference(ForegroundProperty,"PrimaryText");SetResourceReference(BackgroundProperty,"PanelBackground");
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var header=new ChromeHeader{Content=heading,MinHeight=52};heading.SetResourceReference(TextBlock.FontSizeProperty,"EditorTitleSize");root.Children.Add(header);
        Grid.SetRow(videoPane,1);root.Children.Add(videoPane);
        toolsScroll=new(){Content=body,Padding=new(18,12,18,12),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(toolsScroll,2);root.Children.Add(toolsScroll);
        RegisterName("VideoToolsScroll",toolsScroll);SizeChanged+=(_,_)=>UpdateVideoSize();
        var footer=new Grid{Margin=new(18,10,18,12)};footer.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        status.Margin=new(0,0,12,0);status.SetResourceReference(TextBlock.ForegroundProperty,"SecondaryText");footer.Children.Add(status);Grid.SetColumn(apply,1);footer.Children.Add(apply);
        var footerLine=new Border{Child=footer,BorderThickness=new(0,1,0,0)};footerLine.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");Grid.SetRow(footerLine,3);root.Children.Add(footerLine);
        apply.SetResourceReference(Control.StyleProperty,"PrimaryButton");
        apply.Click+=(_,_)=>Run(ApplyAsync);apply.IsDefault=true;apply.IsEnabled=false;
        var surface=new MaterialSurface{Child=root};Content=surface;
        WindowChromeSetup.Apply(this);
        (Application.Current as App)?.AttachPresentation(this,surface);
        RegisterName("ApplyButton",apply);RegisterName("StatusText",status);
        previewDelay.Tick+=(_,_)=>{previewDelay.Stop();Run(RefreshPreviewAsync);};
        PreviewKeyDown+=KeyPressed;
        Closed+=(_,_)=>{closed=true;previewDelay.Stop();lifetime.Cancel();previewWork?.Cancel();playback?.Stop();};
    }
    public async Task<EditorSubmissionBatch?> ShowEditorAsync(EditorOpenRequest value,CancellationToken ct)
    {
        if(shown)throw new InvalidOperationException("An editor can only be shown once.");shown=true;request=value;
        if(!value.Operation.Id.StartsWith("video.",StringComparison.Ordinal)){Width=540;Height=600;}
        Title=heading.Text=ToolTitle(value.Operation.Id,value.Operation.Label);apply.Content=ActionLabel(value.Operation.Id);
        Loaded+=Load;
        using var cancellation=ct.Register(()=>Dispatcher.BeginInvoke(new Action(()=>{if(!closed)Close();})));
        try {ct.ThrowIfCancellationRequested();ShowDialog();return accepted;}
        finally
        {
            lifetime.Cancel();await loading;await Task.WhenAll(work.ToArray());
            if(transport is not null)transport.Controller=null;
            if(playback is not null)await playback.DisposeAsync();
            if(sessionProbe is not null)await sessionProbe.DisposeAsync();
            lifetime.Dispose();if(Directory.Exists(previewRoot))Directory.Delete(previewRoot,true);
        }
        void Load(object sender,RoutedEventArgs e){Loaded-=Load;loading=Guard(LoadAsync);}
    }
    private async Task LoadAsync()
    {
        status.Text="Preparing media…";
        sessionProbe=new(engine.Process,engine.Sources,Path.Combine(previewRoot,"session-sources"));
        foreach(var result in await engine.Sources.CaptureAsync(request.InitialPaths,lifetime.Token))
        {
            var source=result.Snapshot??throw new IOException(result.Error);
            var inspected=await sessionProbe.InspectAsync(source,lifetime.Token);
            var doc=engine.CreateDocument(request.Operation,source,inspected,request.InitialOptions,request.DestinationDirectory);
            sources.Add(source);documents.Add(doc);initialDocuments.Add(doc);histories.Add(new(doc));
        }
        if(documents.Count==0)throw new ArgumentException("Choose a local media file.");
        document=documents[0];playback=new(new MediaPlaybackPreparation(engine.Process,engine.Sources,previewRoot,new(960,640),sessionProbe,knownCapabilities:engine.Capabilities),()=>new WasapiPreviewSink());
        await ShowSourceAsync(0);
    }
    private async Task ShowSourceAsync(int index)
    {
        if(index<0||index>=documents.Count)return;
        if(index!=selected&&IsReady)
        {
            commitResizeFields?.Invoke();
            if(invalidFields.Any(field=>field.IsEnabled)||invalidVideoSize||!CommitTimes()){if(sourcePicker is not null)sourcePicker.SelectedIndex=selected;return;}
        }
        long ticket=++sourceGeneration;
        ++viewGeneration;previewDelay.Stop();previewWork?.Cancel();playback?.Stop();IsReady=false;apply.IsEnabled=false;body.IsEnabled=false;
        documents[selected]=document;selected=index;document=documents[index];
        MediaSourceInfo inspected;
        try{inspected=await sessionProbe!.InspectAsync(sources[index],lifetime.Token);}
        catch(Exception error)when(error is not OutOfMemoryException and not OperationCanceledException)
        {
            if(ticket==sourceGeneration&&!closed)
            {
                // Keep the failed source inadmissible while allowing a return to a valid file.
                // The retained editor belongs to the previous source and must stay inert.
                body.IsEnabled=true;
                foreach(UIElement child in body.Children)child.IsEnabled=ReferenceEquals(child,sourcePicker);
                if(transport is not null)transport.IsEnabled=false;
                string recovery=sourcePicker is null?"Close this editor and choose the files again.":"Choose another file to continue.";
                status.Text=Path.GetFileName(sources[index].Path)+": "+error.Message+" "+recovery;UpdateAdmission();
            }
            return;
        }
        if(ticket!=sourceGeneration||closed)return;
        info=inspected;
        foreach(string name in new[]{"FullTimeline","VideoTransport","VideoTrimSelection","ResizeOptions","ResizeWidth","ResizeHeight","PreserveVideoAspect"})if(FindName(name) is not null)UnregisterName(name);
        invalidVideoSize=false;invalidFields.Clear();commitResizeFields=null;
        if(transport is not null)transport.Controller=null;
        videoPane.Content=null;videoPane.Visibility=Visibility.Collapsed;
        body.Children.Clear();channelViews.Clear();range=null;videoTrim=null;transport=null;metadataRows=null;startField=endField=null;visualizationPreview=null;fullTimeline=null;auditionReadout=null;sourcePicker=null;
        if(documents.Count>1)
        {
            var picker=new ComboBox{ItemsSource=sources.Select(s=>Path.GetFileName(s.Path)).ToArray(),SelectedIndex=selected,Margin=new(0,0,0,12)};
            sourcePicker=picker;
            picker.SelectionChanged+=(_,_)=>{if(picker.SelectedIndex!=selected)Run(()=>ShowSourceAsync(picker.SelectedIndex));};body.Children.Add(picker);
            body.Children.Add(new TextBlock{Text=$"{documents.Count} files · editing selected file",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)});
        }
        body.Children.Add(new TextBlock{Text=Path.GetFileName(sources[index].Path),FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)});
        if(document.Recipe is VideoRecipe)
        {
            var view=new MediaTransport{Controller=playback,VideoMode=true,ExternalTimeline=true};transport=view;
            if(info.Audio.FirstOrDefault()?.Channels.Length>2)view.PreviewNotice="Surround preview is silent; saved channels are unchanged.";
            view.WindowRequested=(position,play)=>Track(async()=>{if(ticket!=sourceGeneration||closed||!ReferenceEquals(transport,view))return;view.SetTimelinePosition(position);await RefreshPreviewAsync(play);});
            view.FrameRequested=direction=>Track(()=>StepVideoFrameAsync(view,ticket,direction));
            RegisterName("VideoTransport",transport);videoPane.Content=transport;videoPane.Visibility=Visibility.Visible;UpdateVideoSize();
        }
        if(document.Recipe is VideoRecipe||Id is not "audio.metadata" and not "audio.compress")BuildNavigation();
        switch(Id)
        {
            case "audio.trim":case "video.trim":BuildTrim();break;
            case "audio.bleep":BuildBleep();break;
            case "audio.channels":BuildChannels();break;
            case "audio.normalize":BuildNormalize();break;
            case "video.speed":BuildSpeed();break;
            case "video.mute":BuildMute();break;
            case "video.snapshot":BuildSnapshot();break;
            case "video.split":BuildSplit();break;
            case "video.redact":BuildRedaction();break;
            case "video.resize":BuildResize();break;
            case "audio.compress":case "video.compress":BuildCompression();break;
            case "audio.metadata":case "video.metadata":await BuildMetadataAsync(ticket);break;
            case "audio.visualizer":BuildVisualizer();break;
        }
        if(ticket!=sourceGeneration||closed)return;
        if(document.Recipe is AudioRecipe or VisualizerRecipe)body.Children.Add(Button("Stop preview",()=>playback?.Stop()));
        body.Children.Add(Button("Reset",()=>{histories[selected].Reset();document=histories[selected].Current;documents[selected]=document;resizeAspect.Remove(sources[selected].Id);Run(()=>ShowSourceAsync(selected));}));
        await RefreshPreviewAsync();if(ticket!=sourceGeneration||closed)return;body.IsEnabled=true;IsReady=true;status.Text="";UpdateAdmission();
    }
    private void BuildTrim()
    {
        if(document.Recipe is VideoRecipe)
        {
            var videoClip=Clips()[0];
            videoTrim=new(){Duration=VideoDuration,SelectionStart=Seconds(videoClip.Range.Start),SelectionEnd=Seconds(videoClip.Range.End)};
            RegisterName("VideoTrimSelection",videoTrim);transport!.TimelineAdornment=videoTrim;transport.TimelineControl.Visibility=Visibility.Collapsed;UpdateVideoSize();
            videoTrim.DragStateChanged+=UpdateAdmission;
            videoTrim.SelectionChanged+=(start,end,commit)=>
            {
                durationText!.Text=MediaRangeView.Timecode(end-start)+" selected";
                if(commit)SetRecipe(Trim(document.Recipe,Time(start),Time(end)),"trim"+Guid.NewGuid().ToString("N"));
                if(fullTimeline is not null)fullTimeline.Value=start;
                previewDelay.Stop();previewDelay.Start();
            };
            videoTrim.SeekRequested+=position=>{if(fullTimeline is not null)fullTimeline.Value=position;};
            durationText=Label(MediaRangeView.Timecode(videoTrim.SelectionEnd-videoTrim.SelectionStart)+" selected");body.Children.Add(durationText);
            var keep=new WrapPanel();
            keep.Children.Add(Button("Start here",()=>videoTrim.SetHandle(true,CurrentPlayhead(),true)));
            keep.Children.Add(Button("End here",()=>videoTrim.SetHandle(false,CurrentPlayhead(),true)));body.Children.Add(keep);
            return;
        }
        var clip=Clips()[0];range=new(){Duration=Seconds(info.Duration),SelectionStart=Seconds(clip.Range.Start),SelectionEnd=Seconds(clip.Range.End),Height=150};
        range.TrimChanged+=(start,end,commit)=>
        {
            if(!commit) {UpdateTimes(start,end);return;}
            SetRecipe(Trim(document.Recipe,Time(start),Time(end)),"trim");UpdateTimes(start,end);Run(RefreshPreviewAsync);
        };
        body.Children.Add(range);durationText=Label("");body.Children.Add(durationText);
        var row=new WrapPanel{Margin=new(0,10,0,14)};row.Children.Add(Label("Start"));startField=Field(MediaRangeView.Timecode(range.SelectionStart),"Trim start");startField.Width=110;row.Children.Add(startField);row.Children.Add(Label("End"));endField=Field(MediaRangeView.Timecode(range.SelectionEnd),"Trim end");endField.Width=110;row.Children.Add(endField);body.Children.Add(row);
        startField.LostKeyboardFocus+=(_,_)=>CommitTimes();endField.LostKeyboardFocus+=(_,_)=>CommitTimes();
        if(Id=="audio.trim")
        {
            var silent=new CheckBox{Content="Trim silent ends automatically",Margin=new(0,0,0,12)};
            silent.Checked+=(_,_)=>Run(async()=>
            {
                var captured=document;long ticket=sourceGeneration;var target=range;
                var selectedRange=await engine.FindAudibleRangeAsync(initialDocuments[selected],lifetime.Token);
                if(closed||ticket!=sourceGeneration||document!=captured||!ReferenceEquals(range,target)||target is null)return;
                SetRecipe(Trim(document.Recipe,selectedRange.Start,selectedRange.End),"silence");target.SelectionStart=Seconds(selectedRange.Start);target.SelectionEnd=Seconds(selectedRange.End);target.InvalidateVisual();UpdateTimes(target.SelectionStart,target.SelectionEnd);
            });body.Children.Add(silent);
            body.Children.Add(Button("▶ Play Selected Range",()=>Run(()=>PlayAsync(document,false))));
        }
        UpdateTimes(range.SelectionStart,range.SelectionEnd);
    }
    private void BuildBleep()
    {
        var audio=(AudioRecipe)document.Recipe;
        bleepCount=Label($"Bleeps {audio.Bleeps.Length}");body.Children.Add(bleepCount);
        body.Children.Add(Label("Drag to add a bleep; drag its edges to resize."));
        range=new(){BleepLane=true,Duration=MediaEditorEngine.Duration(document),Height=180,Regions=audio.Bleeps.Select(b=>new MediaInterval(b.Id,Seconds(b.Range.Start),Seconds(b.Range.End))).ToImmutableArray()};
        Guid? selectedId=selectedBleeps.TryGetValue(sources[selected].Id,out var saved)&&audio.Bleeps.Any(b=>b.Id==saved)?saved:audio.Bleeps.FirstOrDefault()?.Id;
        range.SelectRegion(selectedId);selectedBleeps[sources[selected].Id]=selectedId;
        range.SelectionChanged+=id=>{selectedBleeps[sources[selected].Id]=id;SyncBleepControls();};
        range.RegionsChanged+=(regions,commit)=>
        {
            if(!commit)return;
            var current=(AudioRecipe)document.Recipe;
            var bleeps=regions.Select(r=>current.Bleeps.FirstOrDefault(b=>b.Id==r.Id) is {} existing?existing with {Range=new(Time(r.Start),Time(r.End))}:new BleepRegion(r.Id,new(Time(r.Start),Time(r.End)),BleepMode.Tone,1000,-12,5,Enumerable.Range(0,current.Channels.OutputChannels.Length).ToImmutableArray())).ToImmutableArray();
            SetRecipe(current with {Bleeps=bleeps},"bleep-range");bleepCount.Text=$"Bleeps {bleeps.Length}";SyncBleepControls();UpdateAdmission();
        };body.Children.Add(range);
        var row=new WrapPanel{Margin=new(0,12,0,12)};row.Children.Add(Button("Add range",()=>
        {
            var a=(AudioRecipe)document.Recipe;double total=MediaEditorEngine.Duration(document),start=Math.Min(CurrentPlayhead(),Math.Max(0,total-.01)),end=Math.Min(total,start+Math.Min(1,total/4));
            var next=a.Bleeps.Add(new(Guid.NewGuid(),new(Time(start),Time(end)),BleepMode.Tone,1000,-12,5,Enumerable.Range(0,a.Channels.OutputChannels.Length).ToImmutableArray()));SetRecipe(a with {Bleeps=next},"add-bleep");range.Regions=next.Select(b=>new MediaInterval(b.Id,Seconds(b.Range.Start),Seconds(b.Range.End))).ToImmutableArray();range.SelectRegion(next[^1].Id);range.InvalidateVisual();bleepCount.Text=$"Bleeps {next.Length}";
        }));row.Children.Add(Button("Remove selected",()=>range.RemoveSelected()));body.Children.Add(row);
        var preview=new WrapPanel{Margin=new(0,4,0,14)};preview.Children.Add(Label("Preview"));preview.Children.Add(Button("Original",()=>Run(()=>PlayAsync(document,true))));preview.Children.Add(Button("▶ With Bleeps",()=>Run(()=>PlayAsync(document,false))));body.Children.Add(preview);
        var active=audio.Bleeps.FirstOrDefault(b=>b.Id==range.SelectedRegion)??audio.Bleeps.FirstOrDefault();
        bleepFrequency=AddSlider("Tone frequency",100,4000,active?.FrequencyHz??1000,"Hz",value=>ChangeBleep(b=>b with {FrequencyHz=value}));
        bleepGain=AddSlider("Tone level",-60,-3,active?.GainDbFs??-12,"dBFS",value=>ChangeBleep(b=>b with {GainDbFs=value}));
        bleepMode=new ComboBox{ItemsSource=new[]{"Tone","Silence"},SelectedIndex=active?.Mode==BleepMode.Silence?1:0,Margin=new(0,0,0,12)};bleepMode.SelectionChanged+=(_,_)=>{if(!hydratingControls)ChangeBleep(b=>b with {Mode=bleepMode.SelectedIndex==1?BleepMode.Silence:BleepMode.Tone});};body.Children.Add(bleepMode);
    }
    private void SyncBleepControls()
    {
        if(document.Recipe is not AudioRecipe audio||bleepFrequency is null||bleepGain is null||bleepMode is null)return;
        var active=audio.Bleeps.FirstOrDefault(b=>b.Id==range?.SelectedRegion)??audio.Bleeps.FirstOrDefault();
        hydratingControls=true;
        try{bleepFrequency.Value=active?.FrequencyHz??1000;bleepGain.Value=active?.GainDbFs??-12;bleepMode.SelectedIndex=active?.Mode==BleepMode.Silence?1:0;}
        finally{hydratingControls=false;}
    }
    private void ChangeBleep(Func<BleepRegion,BleepRegion> update)
    {
        var a=(AudioRecipe)document.Recipe;Guid? selectedId=range?.SelectedRegion;
        SetRecipe(a with {Bleeps=a.Bleeps.Select(b=>selectedId is null||b.Id==selectedId?update(b):b).ToImmutableArray()},"bleep-settings");
    }
    private void BuildChannels()
    {
        body.Children.Add(Label("Output channel preview"));var audio=(AudioRecipe)document.Recipe;
        for(int i=0;i<Math.Min(2,audio.Channels.OutputChannels.Length);i++)
        {
            int channel=i;var row=new DockPanel{Margin=new(0,0,0,10)};var play=Button("▶",()=>Run(()=>PlayChannelAsync(channel)));DockPanel.SetDock(play,Dock.Right);row.Children.Add(play);
            row.Children.Add(Label(audio.Channels.OutputChannels[i]));var wave=new WaveformView{Height=56,MinWidth=180};wave.SetResourceReference(Control.ForegroundProperty,"Accent");channelViews.Add(wave);row.Children.Add(wave);body.Children.Add(row);
        }
        var mode=new ListBox{ItemsSource=new[]{"Mono","Stereo"},SelectedIndex=audio.Channels.OutputChannels.Length==1?0:1,Margin=new(0,10,0,12),HorizontalAlignment=HorizontalAlignment.Center,BorderThickness=new(0),Padding=new(4)};
        mode.SetResourceReference(ItemsControl.ItemContainerStyleProperty,"SegmentedItem");
        var horizontal=new FrameworkElementFactory(typeof(StackPanel));horizontal.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);mode.ItemsPanel=new ItemsPanelTemplate(horizontal);
        mode.SelectionChanged+=(_,_)=>
        {
            var current=(AudioRecipe)document.Recipe;int input=current.Channels.InputChannels.Length,output=mode.SelectedIndex+1;
            var matrix=new ChannelMatrix(current.Channels.InputChannels,output==1?["FC"]:["FL","FR"],Enumerable.Range(0,output*input).Select(i=>output==1?1d/input:input==1?1d:i/input==i%input?1d:0d).ToImmutableArray(),false);
            SetRecipe(current with {Channels=matrix,Bleeps=[]},"channels");Run(()=>ShowSourceAsync(selected));
        };body.Children.Add(mode);
        for(int i=0;i<Math.Min(2,audio.Channels.OutputChannels.Length);i++)
        {
            int channel=i;
            double initialGain=audio.Channels.Coefficients.Skip(channel*audio.Channels.InputChannels.Length).Take(audio.Channels.InputChannels.Length).Sum()*100;
            AddSlider(audio.Channels.OutputChannels[i]+" gain",0,200,initialGain,"%",value=>
            {
                var current=(AudioRecipe)document.Recipe;int count=current.Channels.InputChannels.Length;
                var coefficients=current.Channels.Coefficients.ToArray();double scale=value/100d;
                for(int input=0;input<count;input++)coefficients[channel*count+input]=current.Channels.OutputChannels.Length==1?scale/count:count==1?scale:channel==input?scale:0;
                SetRecipe(current with {Channels=current.Channels with {Coefficients=[..coefficients],LimitPeak=value>100||current.Channels.LimitPeak}},"gain"+channel);
            });
        }
        body.Children.Add(Button("▶ Play Output",()=>Run(()=>PlayAsync(document,false))));
    }
    private void BuildNormalize()
    {
        var audio=(AudioRecipe)document.Recipe;
        AddSlider("Integrated loudness",-70,-5,audio.Loudness?.IntegratedLufs??-16,"LUFS",value=>SetRecipe(((AudioRecipe)document.Recipe) with {Loudness=new(value,11,-1.5)},"loudness"));
        body.Children.Add(Button("▶ Play Output",()=>Run(()=>PlayAsync(document,false))));
    }
    private void BuildSpeed()
    {
        AddSlider("Speed",.25,4,((VideoRecipe)document.Recipe).Clips[0].Speed,"×",value=>{var video=(VideoRecipe)document.Recipe;SetRecipe(video with {Clips=video.Clips.Select(c=>c with {Speed=Math.Round(value,6)}).ToImmutableArray()},"speed");});
    }
    private void BuildMute()
    {
        var mute=new CheckBox{Content="Remove audio track",IsChecked=((VideoRecipe)document.Recipe).Mute,Margin=new(0,0,0,16)};
        mute.Click+=(_,_)=>{SetRecipe(((VideoRecipe)document.Recipe) with {Mute=mute.IsChecked==true},"mute");Run(RefreshPreviewAsync);};body.Children.Add(mute);
    }
    private void BuildSnapshot()
    {
        body.Children.Add(Button("Use current frame",()=>{var v=(VideoRecipe)document.Recipe;var position=Time(Math.Max(0,Math.Min(CurrentPlayhead(),MediaEditorEngine.Duration(document)-.000001)));SetRecipe(v with {SnapshotAt=position},"snapshot");status.Text="Snapshot at "+MediaRangeView.Timecode(Seconds(position));}));
        string[] formats=["PNG","JPG","WEBP"];string current=document.Export.FormatId=="jpeg"?"JPG":document.Export.FormatId.ToUpperInvariant();
        var format=new ComboBox{ItemsSource=formats,SelectedIndex=Array.IndexOf(formats,current),Margin=new(0,12,0,12)};format.SelectionChanged+=(_,_)=>SetExport(document.Export with {FormatId=((string)format.SelectedItem).ToLowerInvariant()},"snapshot-format");body.Children.Add(format);
    }
    private void BuildSplit()
    {
        var list=new ListBox{Height=140,ItemsSource=((VideoRecipe)document.Recipe).SplitAt.Select(t=>MediaRangeView.Timecode(Seconds(t))).ToArray()};body.Children.Add(list);
        var row=new WrapPanel{Margin=new(0,12,0,12)};row.Children.Add(Button("Add split at playhead",()=>
        {
            var video=(VideoRecipe)document.Recipe;var time=Time(CurrentPlayhead());
            if(Seconds(time)<=0||Seconds(time)>=MediaEditorEngine.Duration(document)) {status.Text="Choose a point inside the video.";return;}
            var times=video.SplitAt.Append(time).Distinct().OrderBy(t=>t).ToImmutableArray();SetRecipe(video with {SplitAt=times},"split");list.ItemsSource=times.Select(t=>MediaRangeView.Timecode(Seconds(t))).ToArray();
        }));row.Children.Add(Button("Remove split",()=>{int index=list.SelectedIndex;if(index<0)return;var video=(VideoRecipe)document.Recipe;SetRecipe(video with {SplitAt=video.SplitAt.RemoveAt(index)},"remove-split");list.ItemsSource=((VideoRecipe)document.Recipe).SplitAt.Select(t=>MediaRangeView.Timecode(Seconds(t))).ToArray();}));body.Children.Add(row);
    }
    private void BuildRedaction()
    {
        body.Children.Add(Label("Manual redaction; no tracking."));
        var video=(VideoRecipe)document.Recipe;var plane=info.Video[0].DisplaySize;
        var bounds=video.Masks.FirstOrDefault()?.Keys[0].Bounds??new RectD(plane.Width/4,plane.Height/4,Math.Max(2,plane.Width/2),Math.Max(2,plane.Height/2));
        if(video.Masks.IsEmpty) SetRecipe(video with {Masks=[new(Guid.NewGuid(),new(new(0,1),MediaEditorEngine.Time(MediaEditorEngine.Duration(document))),MaskEffect.Solid,new(0,0,0,255),12,[new(new(0,1),bounds)],MaskInterpolation.Step)]},"initial-mask");
        if(transport?.FindName("Preview") is System.Windows.Controls.Image image&&image.Parent is Grid grid)
        {
            var overlay=new CropOverlay{Plane=plane,Crop=bounds};
            overlay.Changed+=(rect,commit)=>{if(!commit)return;var v=(VideoRecipe)document.Recipe;SetRecipe(v with {Masks=v.Masks.Select(m=>m with {Keys=[new(m.Range.Start,rect)]}).ToImmutableArray()},"mask");previewDelay.Stop();previewDelay.Start();};
            grid.Children.Add(overlay);Grid.SetRow(overlay,0);
        }
        var effect=new ComboBox{ItemsSource=new[]{"Solid","Blur","Pixelate"},SelectedIndex=(int)((VideoRecipe)document.Recipe).Masks[0].Effect,Margin=new(0,12,0,12)};
        effect.SelectionChanged+=(_,_)=>{var v=(VideoRecipe)document.Recipe;SetRecipe(v with {Masks=v.Masks.Select(m=>m with {Effect=(MaskEffect)effect.SelectedIndex}).ToImmutableArray()},"mask-effect");previewDelay.Stop();previewDelay.Start();};body.Children.Add(effect);
    }
    private void BuildResize()
    {
        long ticket=sourceGeneration;
        body.Children.Add(Label("Output size"));
        var source=info.Video[0].DisplaySize;
        var size=((VideoRecipe)document.Recipe).Canvas;
        var width=Field(size.Width.ToString(CultureInfo.InvariantCulture),"Video output width in pixels");
        var height=Field(size.Height.ToString(CultureInfo.InvariantCulture),"Video output height in pixels");
        var preserve=new CheckBox{Content="Preserve aspect ratio",IsChecked=resizeAspect.GetValueOrDefault(sources[selected].Id,request.InitialOptions.PreserveAspectRatio),Margin=new(0,4,0,8)};
        RegisterName("ResizeWidth",width);RegisterName("ResizeHeight",height);RegisterName("PreserveVideoAspect",preserve);
        var readout=Label($"{size.Width:N0} × {size.Height:N0} pixels");
        bool updating=false;
        var choices=new[]{1d,.75,.5,.25};
        var row=new WrapPanel();
        foreach(double scale in choices)
        {
            double selectedScale=scale;
            row.Children.Add(Button((scale*100).ToString("0",CultureInfo.InvariantCulture)+"%",()=>
            {
                preserve.IsChecked=true;resizeAspect[sources[selected].Id]=true;
                ApplySize(new(Math.Max(2,(int)(source.Width*selectedScale)/2*2),Math.Max(2,(int)(source.Height*selectedScale)/2*2)));
            }));
        }
        body.Children.Add(row);
        body.Children.Add(readout);
        var fields=new Grid();fields.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});fields.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        var widthGroup=new StackPanel();widthGroup.Children.Add(Label("Width · px"));widthGroup.Children.Add(width);fields.Children.Add(widthGroup);
        var heightGroup=new StackPanel();heightGroup.Children.Add(Label("Height · px"));heightGroup.Children.Add(height);Grid.SetColumn(heightGroup,1);fields.Children.Add(heightGroup);
        var custom=new StackPanel();custom.Children.Add(fields);custom.Children.Add(preserve);custom.Children.Add(Label("MP4 uses even dimensions from 2 to 8190 pixels."));
        var options=new Expander{Header="More options",Content=custom,Margin=new(0,4,0,8)};RegisterName("ResizeOptions",options);body.Children.Add(options);
        width.TextChanged+=(_,_)=>ValidateFields();height.TextChanged+=(_,_)=>ValidateFields();
        width.LostKeyboardFocus+=(_,_)=>Commit(true);height.LostKeyboardFocus+=(_,_)=>Commit(false);
        commitResizeFields=()=>Commit(Keyboard.FocusedElement==height?false:Keyboard.FocusedElement==width?true:null);
        width.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit(true);e.Handled=true;}};height.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Commit(false);e.Handled=true;}};
        preserve.Click+=(_,_)=>Commit(null);
        bool Read(out int w,out int h){bool a=int.TryParse(width.Text,NumberStyles.None,CultureInfo.InvariantCulture,out w),b=int.TryParse(height.Text,NumberStyles.None,CultureInfo.InvariantCulture,out h);return a&&b&&w is >=2 and <=8190&&h is >=2 and <=8190;}
        void ValidateFields()
        {
            if(updating||ticket!=sourceGeneration)return;
            invalidVideoSize=!Read(out _,out _);if(invalidVideoSize)status.Text="Enter pixel dimensions from 2 to 8190.";else if(status.Text=="Enter pixel dimensions from 2 to 8190.")status.Text="";UpdateAdmission();
        }
        void Commit(bool? widthChanged)
        {
            if(ticket!=sourceGeneration)return;
            if(updating||!Read(out int w,out int h)){ValidateFields();return;}
            bool keep=preserve.IsChecked==true;resizeAspect[sources[selected].Id]=keep;
            var requested=request.InitialOptions with {Width=keep&&widthChanged==false?null:w,Height=keep&&widthChanged==true?null:h,PreserveAspectRatio=keep};
            try
            {
                var draft=engine.CreateDocument(request.Operation,sources[selected],info,requested,request.DestinationDirectory);
                ApplySize(((VideoRecipe)draft.Recipe).Canvas);
            }
            catch(ArgumentException error)
            {
                invalidVideoSize=true;status.Text=error.Message;UpdateAdmission();
            }
        }
        void ApplySize(SizeI canvas)
        {
            updating=true;
            try{width.Text=canvas.Width.ToString(CultureInfo.InvariantCulture);height.Text=canvas.Height.ToString(CultureInfo.InvariantCulture);}
            finally{updating=false;}
            invalidVideoSize=false;status.Text="";readout.Text=$"{canvas.Width:N0} × {canvas.Height:N0} pixels";
            var current=(VideoRecipe)document.Recipe;if(current.Canvas!=canvas){SetRecipe(current with {Canvas=canvas},"resize");previewDelay.Stop();previewDelay.Start();}UpdateAdmission();
        }
    }
    private void BuildCompression()
    {
        long original=sources[selected].Length;body.Children.Add(Label($"Original size: {Size(original)}"));
        var mode=new ComboBox{ItemsSource=new[]{"Percentage","Target size","Quality"},SelectedIndex=document.Export.Compression is ByteGoal?1:document.Export.Compression is QualityGoal?2:0,Margin=new(0,8,0,16)};
        body.Children.Add(Label("Mode"));body.Children.Add(mode);
        string initial=document.Export.Compression switch {QualityGoal quality=>quality.Value.ToString(CultureInfo.InvariantCulture),ByteGoal bytes=>(bytes.MaximumBytes/1024d).ToString("0.##",CultureInfo.InvariantCulture),ReductionGoal reduction=>reduction.Percent.ToString(CultureInfo.InvariantCulture),_=>"50"};
        var inputLabel=Label("");body.Children.Add(inputLabel);
        var input=Field(initial,"Compression value");body.Children.Add(input);var explanation=Label("");body.Children.Add(explanation);
        var slider=new Slider{Minimum=1,Maximum=100,Margin=new(0,12,0,12)};body.Children.Add(slider);
        bool updating=false;
        void ShowGoal(CompressionGoal goal)
        {
            inputLabel.Text=goal switch {ReductionGoal=>"Reduce by (%)",ByteGoal=>"Maximum output size (KB)",_=>"Quality (1–100)"};
            long? target=goal switch {ReductionGoal r=>(long)decimal.Floor(original*(1-r.Percent/100)),ByteGoal b=>b.MaximumBytes,_=>null};
            explanation.Text=target is {} maximum?$"Maximum size: {Size(maximum)}.":"Size varies with quality.";
            updating=true;
            try
            {
                slider.Visibility=goal is ByteGoal?Visibility.Collapsed:Visibility.Visible;
                slider.Minimum=goal is ReductionGoal ? .01 : 1;slider.Maximum=goal is ReductionGoal ? 99.99 : 100;
                slider.Value=goal switch {ReductionGoal r=>(double)r.Percent,QualityGoal q=>q.Value,_=>50};
            }
            finally{updating=false;}
        }
        void Change()
        {
            inputLabel.Text=mode.SelectedIndex switch {0=>"Reduce by (%)",1=>"Maximum output size (KB)",_=>"Quality (1–100)"};
            if(!double.TryParse(input.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value)||value<=0||mode.SelectedIndex==0&&value>=100||mode.SelectedIndex==2&&value>100||mode.SelectedIndex==1&&value>long.MaxValue/1024d) {invalidFields.Add(input);apply.IsEnabled=false;explanation.Text=mode.SelectedIndex==0?"Enter a reduction greater than 0 and less than 100%.":mode.SelectedIndex==2?"Enter quality from 1 to 100.":"Enter a positive target size in KB.";return;}
            invalidFields.Remove(input);
            CompressionGoal goal=mode.SelectedIndex switch {0=>new ReductionGoal((decimal)value),1=>new ByteGoal(Math.Max(1,checked((long)(value*1024)))),_=>new QualityGoal((int)Math.Max(1,value))};SetExport(document.Export with {Compression=goal},"compression");
            ShowGoal(goal);
        }
        input.TextChanged+=(_,_)=>Change();mode.SelectionChanged+=(_,_)=>Change();
        slider.ValueChanged+=(_,_)=>{if(updating)return;input.Text=slider.Value.ToString(mode.SelectedIndex==0?"0.##":"0",CultureInfo.InvariantCulture);};
        if(Id=="audio.compress")
        {
            string[] formats=["MP3","M4A","FLAC","WAV","OGG"];
            var format=new ComboBox{ItemsSource=formats,SelectedIndex=Array.IndexOf(formats,document.Export.FormatId.ToUpperInvariant()),Margin=new(0,0,0,12)};format.SelectionChanged+=(_,_)=>SetExport(document.Export with {FormatId=((string)format.SelectedItem).ToLowerInvariant()},"audio-format");body.Children.Add(format);
        }
        ShowGoal(document.Export.Compression);
    }
    private async Task BuildMetadataAsync(long ticket)
    {
        var fields=await engine.ReadMetadataAsync(sources[selected],lifetime.Token);if(ticket!=sourceGeneration||closed)return;metadata=fields;body.Children.Add(Label("Search metadata"));search=Field("","Search metadata");search.TextChanged+=(_,_)=>RenderMetadata();body.Children.Add(search);metadataRows=new(){Margin=new(0,14,0,0)};body.Children.Add(metadataRows);
        RenderMetadata();
        var removeAll=Button("Remove All",()=>{var edits=metadata.Where(f=>f.Removable&&f.Value is not TextValue {Value:""}).Select(f=>new MetadataEdit(f.Scope,f.Key,MetadataEditAction.Remove,null)).ToImmutableArray();SetMetadata(edits,MetadataPolicy.StripAll);RenderMetadata();});removeAll.ToolTip="Remove editable tags";body.Children.Add(removeAll);
    }
    private void RenderMetadata()
    {
        if(metadataRows is null)return;metadataRows.Children.Clear();string query=search?.Text??"";
        var edits=MetadataEdits();
        foreach(var group in metadata.Where(f=>string.IsNullOrWhiteSpace(query)||(f.Scope+" "+f.DisplayName+" "+f.Value).Contains(query,StringComparison.OrdinalIgnoreCase)).GroupBy(f=>f.Scope))
        {
            metadataRows.Children.Add(new TextBlock{Text=group.Key,FontWeight=FontWeights.SemiBold,Margin=new(0,12,0,8)});
            foreach(var field in group)
            {
                if(edits.Any(e=>e.Scope==field.Scope&&e.Key==field.Key&&e.Action==MetadataEditAction.Remove))continue;
                var row=new Grid{Margin=new(0,0,0,10)};row.ColumnDefinitions.Add(new(){Width=new(160)});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
                row.Children.Add(new TextBlock{Text=field.DisplayName,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,12,0)});
                var current=edits.FirstOrDefault(e=>e.Scope==field.Scope&&e.Key==field.Key)?.Value??field.Value;
                string text=current is TextValue content?content.Value:current.ToString()??"";
                if(field.Writable)
                {
                    var value=Field(text,field.DisplayName);Grid.SetColumn(value,1);row.Children.Add(value);
                    value.TextChanged+=(_,_)=>{var values=MetadataEdits().Where(e=>e.Scope!=field.Scope||e.Key!=field.Key).Append(new(field.Scope,field.Key,value.Text.Length==0?MetadataEditAction.Remove:MetadataEditAction.Set,value.Text.Length==0?null:new TextValue(value.Text))).ToImmutableArray();SetMetadata(values,MetadataPolicy.PreserveSupported);};
                }
                else{var value=Label(text);value.ToolTip=text;Grid.SetColumn(value,1);row.Children.Add(value);}
                var remove=Button("−",()=>{var values=MetadataEdits().Where(e=>e.Scope!=field.Scope||e.Key!=field.Key).Append(new(field.Scope,field.Key,MetadataEditAction.Remove,null)).ToImmutableArray();SetMetadata(values,document.Export.Metadata);RenderMetadata();});remove.IsEnabled=field.Removable;Grid.SetColumn(remove,2);row.Children.Add(remove);metadataRows.Children.Add(row);
            }
        }
        if(metadataRows.Children.Count==0)metadataRows.Children.Add(Label("No matching metadata."));
    }
    private void BuildVisualizer()
    {
        var visual=(VisualizerRecipe)document.Recipe;
        visualizationPreview=new(){Height=240,Stretch=Stretch.Uniform,Margin=new(0,0,0,16)};body.Children.Add(visualizationPreview);
        var colorFields=new List<TextBox>();
        var mode=new ComboBox{ItemsSource=new[]{"Waveform","Spectrum","Still waveform"},SelectedIndex=(int)visual.Mode,Margin=new(0,0,0,12)};mode.SelectionChanged+=(_,_)=>
        {
            var v=(VisualizerRecipe)document.Recipe;SetDocument(document with {Recipe=v with {Mode=(VisualizerMode)mode.SelectedIndex},Export=document.Export with {FormatId=mode.SelectedIndex==2?"png":"mp4"}},"visualizer-mode");
            foreach(var field in colorFields)field.IsEnabled=mode.SelectedIndex!=(int)VisualizerMode.Spectrum;
            if(invalidFields.Count>0)status.Text=mode.SelectedIndex==(int)VisualizerMode.Spectrum?"":"Enter a color such as #FF5500.";
            UpdateAdmission();
        };body.Children.Add(mode);
        var sizes=new List<SizeI>{new(1920,1080),new(1080,1920),new(1080,1080)};if(!sizes.Contains(visual.Size))sizes.Add(visual.Size);
        var size=new ComboBox{ItemsSource=sizes.Select(s=>$"{s.Width} × {s.Height}").ToArray(),SelectedIndex=sizes.IndexOf(visual.Size),Margin=new(0,0,0,12)};size.SelectionChanged+=(_,_)=>{var v=(VisualizerRecipe)document.Recipe;SetRecipe(v with {Size=sizes[size.SelectedIndex]},"visualizer-size");};body.Children.Add(size);
        var colors=new WrapPanel();
        foreach(bool foreground in new[]{true,false})
        {
            colors.Children.Add(Label(foreground?"Wave color":"Background"));var color=foreground?visual.Foreground:visual.Background;
            var field=Field($"#{color.R:X2}{color.G:X2}{color.B:X2}",foreground?"Wave color hexadecimal":"Background color hexadecimal");field.Width=100;field.IsEnabled=visual.Mode!=VisualizerMode.Spectrum;
            field.TextChanged+=(_,_)=>
            {
                try
                {
                    if(ColorConverter.ConvertFromString(field.Text) is not Color parsed)throw new FormatException();
                    invalidFields.Remove(field);var rgba=new Rgba32(parsed.R,parsed.G,parsed.B,255);var current=(VisualizerRecipe)document.Recipe;
                    SetRecipe(foreground?current with {Foreground=rgba}:current with {Background=rgba},"visualizer-color");
                    if(invalidFields.Count==0&&status.Text=="Enter a color such as #FF5500.")status.Text="";
                }
                catch(Exception error)when(error is FormatException or NotSupportedException or ArgumentException){invalidFields.Add(field);status.Text="Enter a color such as #FF5500.";apply.IsEnabled=false;}
            };colorFields.Add(field);colors.Children.Add(field);
        }
        body.Children.Add(colors);
        var keep=new CheckBox{Content="Include audio",IsChecked=visual.KeepAudio,Margin=new(0,8,0,14)};keep.Click+=(_,_)=>SetRecipe(((VisualizerRecipe)document.Recipe) with {KeepAudio=keep.IsChecked==true},"visualizer-audio");body.Children.Add(keep);
        body.Children.Add(Button("▶ Play audio",()=>Run(()=>PlayAsync(document with {Recipe=((VisualizerRecipe)document.Recipe).Audio},false))));
    }
    private Task RefreshPreviewAsync()=>RefreshPreviewAsync(false);
    private async Task RefreshPreviewAsync(bool play,RationalTime? exactCursor=null)
    {
        using var refresh=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        previewWork?.Cancel();previewWork=refresh;
        try
        {
        long generation=++viewGeneration,ticket=sourceGeneration;var value=PreviewDocument();var view=transport;
        if(value.Recipe is VisualizerRecipe&&visualizationPreview is not null)
        {
            var frame=await engine.RenderVisualizationPreviewAsync(value,new(generation,new(720,400),null,null,null),refresh.Token);
            if(generation!=viewGeneration||ticket!=sourceGeneration||closed)return;
            var image=System.Windows.Media.Imaging.BitmapSource.Create(frame.Size.Width,frame.Size.Height,96,96,PixelFormats.Bgra32,null,frame.Bgra32.ToArray(),frame.Stride);image.Freeze();visualizationPreview.Source=image;
        }
        if(value.Recipe is VideoRecipe&&view is not null)
        {
            await view.LoadAsync(value,AuditionRange(value,exactCursor),refresh.Token,play);
        }
        if(generation!=viewGeneration||ticket!=sourceGeneration||closed)return;
        if(document.Recipe is not VideoRecipe&&(range is not null||channelViews.Count>0))
        {
            var waveformDocument=Id is "audio.trim" or "video.trim" or "audio.bleep"?initialDocuments[selected]:value;
            var rows=await engine.BuildWaveformsAsync(waveformDocument,1024,refresh.Token);if(generation!=viewGeneration||closed)return;
            if(range is not null&&!rows.IsEmpty){range.Buckets=rows[0];range.InvalidateVisual();}
            for(int i=0;i<Math.Min(rows.Length,channelViews.Count);i++)channelViews[i].Buckets=rows[i];
        }
        }
        finally{if(ReferenceEquals(previewWork,refresh))previewWork=null;}
    }
    private async Task PlayAsync(EditorDocument value,bool original)
    {
        await playback!.PlayAsync(value,AuditionRange(value),original,lifetime.Token);
    }
    private async Task StepVideoFrameAsync(MediaTransport view,long ticket,int direction)
    {
        if(ticket!=sourceGeneration||closed||!ReferenceEquals(transport,view)||playback is null)return;
        previewDelay.Stop();previewWork?.Cancel();view.PauseForSeek();
        var value=PreviewDocument();var clip=((VideoRecipe)value.Recipe).Clips[0];
        var at=VideoFrameNavigation.SourcePosition(clip,view.PresentedTime??playback.Position);
        if(at.CompareTo(clip.Range.End)>=0)at=info.Video[0].LastFrame!.SourceOffset;
        var neighbor=await sessionProbe!.GetAdjacentFrameAsync(clip.SourceId,clip.VideoStream!.Value,clip.Range,at,direction,lifetime.Token);
        if(neighbor is null||ticket!=sourceGeneration||closed||!ReferenceEquals(transport,view))return;
        var position=VideoFrameNavigation.OutputPosition(clip,neighbor.SourceOffset);view.SetTimelinePosition(Seconds(position));
        if(Id=="video.snapshot")SetRecipe(((VideoRecipe)document.Recipe) with {SnapshotAt=position},"snapshot-position");
        await RefreshPreviewAsync(false,position);
    }
    private void BuildNavigation()
    {
        bool video=document.Recipe is VideoRecipe;
        double duration=video?VideoDuration:MediaEditorEngine.Duration(document);
        double initial=document.Recipe is VideoRecipe {SnapshotAt:{} snapshot}?Seconds(snapshot):playheads.TryGetValue(sources[selected].Id,out double saved)?saved:0;
        fullTimeline=video?transport!.TimelineControl:new Slider();
        fullTimeline.Minimum=0;fullTimeline.Maximum=duration;fullTimeline.Value=Math.Clamp(initial,0,duration);fullTimeline.SmallChange=.01;fullTimeline.LargeChange=Math.Min(10,duration);fullTimeline.Margin=new(0,0,0,4);
        RegisterName("FullTimeline",fullTimeline);System.Windows.Automation.AutomationProperties.SetName(fullTimeline,"Full timeline position in seconds");
        if(!video)
        {body.Children.Add(Label("Timeline"));body.Children.Add(fullTimeline);auditionReadout=Label("");body.Children.Add(auditionReadout);UpdateAuditionReadout();}
        fullTimeline.ValueChanged+=(_,_)=>
        {
            if(transport?.IsUpdatingTimeline==true)return;
            playheads[sources[selected].Id]=fullTimeline.Value;if(video)transport!.PauseForSeek();else playback?.Stop();UpdateAuditionReadout();
            if(Id=="video.snapshot")
            {
                var video=(VideoRecipe)document.Recipe;
                var at=Time(Math.Max(0,Math.Min(fullTimeline.Value,MediaEditorEngine.Duration(document)-.000001)));
                if(video.SnapshotAt!=at)SetRecipe(video with {SnapshotAt=at},"snapshot-position");
            }
            if(document.Recipe is VideoRecipe){previewDelay.Stop();previewDelay.Start();}
        };
        if(!video&&duration>120)
        {
            var windows=new WrapPanel{Margin=new(0,0,0,10)};
            windows.Children.Add(Button("Previous window",()=>fullTimeline.Value=Math.Max(0,fullTimeline.Value-120)));
            windows.Children.Add(Button("Next window",()=>fullTimeline.Value=Math.Min(Math.Max(0,duration-1),fullTimeline.Value+120)));
            body.Children.Add(windows);
        }
    }
    private TimeRange AuditionRange(EditorDocument value,RationalTime? exactCursor=null)
    {
        double total=MediaEditorEngine.Duration(value),cursor=fullTimeline?.Value??0;
        if(value.Recipe is VideoRecipe video)
        {
            double last=Seconds(info.Video[0].LastFrame!.SourceOffset)/video.Clips[0].Speed;
            double start=cursor>=total?Math.Min(last,Math.Max(0,total-.000001)):Math.Clamp(cursor,0,Math.Max(0,total-.000001));
            return new(exactCursor??Time(start),Time(Math.Min(total,start+15)));
        }
        double audioStart=Math.Clamp(cursor,0,Math.Max(0,total-Math.Min(1,total)));
        return new(Time(audioStart),Time(Math.Min(total,audioStart+120)));
    }
    private double VideoDuration=>Id=="video.trim"?Seconds(info.Video[0].PresentationEnd??info.Duration):MediaEditorEngine.Duration(document);
    private EditorDocument PreviewDocument()
    {
        var value=Id=="video.trim"?document with {Recipe=((VideoRecipe)document.Recipe) with {Clips=((VideoRecipe)initialDocuments[selected].Recipe).Clips}}:document;
        return info.Audio.FirstOrDefault()?.Channels.Length>2&&value.Recipe is VideoRecipe video
            ?value with {Recipe=video with {Mute=true,Clips=video.Clips.Select(clip=>clip with {AudioStream=null}).ToImmutableArray()}}:value;
    }
    private void UpdateVideoSize()
    {
        if(transport is null)return;
        transport.Height=Math.Clamp((ActualHeight>0?ActualHeight:Height)*.73-92,200,304)+(videoTrim is null?0:24);
    }
    private double CurrentPlayhead()=>playback?.State==MediaPlaybackState.Playing?Seconds(playback.Position):fullTimeline?.Value??(playback is null?0:Seconds(playback.Position));
    private void UpdateAuditionReadout()
    {
        if(auditionReadout is null)return;var selection=AuditionRange(document);
        auditionReadout.Text=$"{MediaRangeView.Timecode(Seconds(selection.Start))} – {MediaRangeView.Timecode(Seconds(selection.End))} / {MediaRangeView.Timecode(MediaEditorEngine.Duration(document))}";
    }
    private async Task PlayChannelAsync(int channel)
    {
        var audio=(AudioRecipe)document.Recipe;int inputs=audio.Channels.InputChannels.Length;
        var matrix=new ChannelMatrix(audio.Channels.InputChannels,["FC"],audio.Channels.Coefficients.Skip(channel*inputs).Take(inputs).ToImmutableArray(),audio.Channels.LimitPeak);
        var audition=document with {Recipe=audio with {Channels=matrix,Bleeps=[]}};await PlayAsync(audition,false);
    }
    private void UpdateTimes(double start,double end)
    {
        if(startField is not null)startField.Text=MediaRangeView.Timecode(start);if(endField is not null)endField.Text=MediaRangeView.Timecode(end);if(durationText is not null)durationText.Text=MediaRangeView.Timecode(end-start)+" selected";
    }
    private bool CommitTimes()
    {
        if(startField is null||endField is null)return true;
        if(!TimeSpan.TryParseExact(startField.Text,new[]{@"m\:ss\.ff",@"h\:mm\:ss\.ff",@"m\:ss",@"h\:mm\:ss"},CultureInfo.InvariantCulture,out var start)||!TimeSpan.TryParseExact(endField.Text,new[]{@"m\:ss\.ff",@"h\:mm\:ss\.ff",@"m\:ss",@"h\:mm\:ss"},CultureInfo.InvariantCulture,out var end)||start<TimeSpan.Zero||end<=start||end.TotalSeconds>Seconds(info.Duration))
        {status.Text="Enter start and end inside the source duration, such as 0:01.25.";apply.IsEnabled=false;return false;}
        SetRecipe(Trim(document.Recipe,Time(start.TotalSeconds),Time(end.TotalSeconds)),"trim-times");if(range is not null){range.SelectionStart=start.TotalSeconds;range.SelectionEnd=end.TotalSeconds;range.InvalidateVisual();}return true;
    }
    private void SetRecipe(EditorRecipe recipe,string key)=>SetDocument(document with {Recipe=recipe},key);
    private void SetExport(ExportPolicy policy,string key)=>SetDocument(document with {Export=policy},key);
    private void SetDocument(EditorDocument value,string key)
    {
        if(hydratingControls)return;
        histories[selected].Commit(value,key);document=histories[selected].Current;documents[selected]=document;playback?.Stop();UpdateAdmission();
        if(fullTimeline is not null)
        {
            fullTimeline.Maximum=document.Recipe is VideoRecipe?VideoDuration:MediaEditorEngine.Duration(document);
            playheads[sources[selected].Id]=fullTimeline.Value;UpdateAuditionReadout();
        }
        if(Id is "audio.channels" or "audio.normalize" or "video.speed" or "audio.visualizer") {previewDelay.Stop();previewDelay.Start();}
    }
    private void UpdateAdmission()
    {
        if(documents.Count==0)return;
        var issues=EditorRecipeValidator.Validate(document);bool valid=issues.IsEmpty;
        if(!valid)status.Text=string.Join(" ",issues.Select(i=>i.Message).Distinct());
        if(Id=="audio.bleep"&&document.Recipe is AudioRecipe {Bleeps.IsEmpty:true}){valid=false;status.Text="Add at least one bleep range.";}
        if(Id=="video.split"&&document.Recipe is VideoRecipe {SplitAt.IsEmpty:true}){valid=false;status.Text="Add at least one split point.";}
        if(valid&&status.Text is "Add at least one bleep range." or "Add at least one split point.")status.Text="";
        apply.IsEnabled=valid&&IsReady&&!closed&&!applying&&!invalidVideoSize&&!invalidFields.Any(field=>field.IsEnabled)&&videoTrim?.IsDragging!=true;
    }
    private async Task ApplyAsync()
    {
        if(applying)return;commitResizeFields?.Invoke();if(!CommitTimes()||!apply.IsEnabled)return;
        applying=true;apply.IsEnabled=false;body.IsEnabled=false;documents[selected]=document;
        try
        {
            var submitted=documents.ToArray();
            foreach(var doc in submitted)
            {
                var errors=await engine.ValidateAsync(new(request.Operation,[doc.Sources[0].Path],request.InitialOptions,request.DestinationDirectory,doc),lifetime.Token);
                if(!errors.IsEmpty){status.Text=Path.GetFileName(doc.Sources[0].Path)+": "+string.Join(" ",errors.Select(e=>e.Message));return;}
            }
            var snapshots=sources.ToImmutableArray();accepted=new(request,snapshots,[],[],snapshots.Select(s=>s.Id).ToImmutableArray(),submitted.Select(doc=>new EditorSubmissionItem(Guid.NewGuid(),[doc.Sources[0].Id],doc)).ToImmutableArray());Close();
        }
        finally{applying=false;if(!closed){body.IsEnabled=true;UpdateAdmission();}}
    }
    private void KeyPressed(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape){if(videoTrim?.IsDragging==true)videoTrim.CancelDrag();else Close();e.Handled=true;return;}
        if(applying){e.Handled=true;return;}
        if(e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)return;
        if(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)&&e.Key is Key.Z or Key.Y&&documents.Count>0)
        {
            bool changed=e.Key==Key.Z?histories[selected].Undo():histories[selected].Redo();if(changed){document=histories[selected].Current;documents[selected]=document;Run(()=>ShowSourceAsync(selected));}e.Handled=true;
        }
    }
    private void SetMetadata(ImmutableArray<MetadataEdit> edits,MetadataPolicy policy)
    {
        var recipe=document.Recipe switch {AudioRecipe a=>a with {MetadataEdits=edits},VideoRecipe v=>v with {MetadataEdits=edits},_=>document.Recipe};
        SetDocument(document with {Recipe=recipe,Export=document.Export with {Metadata=policy}},"metadata");
    }
    private ImmutableArray<MetadataEdit> MetadataEdits()=>document.Recipe switch {AudioRecipe a=>a.MetadataEdits,VideoRecipe v=>v.MetadataEdits,_=>[]};
    private ImmutableArray<MediaClip> Clips()=>document.Recipe switch {AudioRecipe a=>a.Clips,VideoRecipe v=>v.Clips,_=>[]};
    private static EditorRecipe Trim(EditorRecipe recipe,RationalTime start,RationalTime end)=>recipe switch {AudioRecipe a=>a with {Clips=a.Clips.Select(c=>c with {Range=new(start,end)}).ToImmutableArray()},VideoRecipe v=>v with {Clips=v.Clips.Select(c=>c with {Range=new(start,end)}).ToImmutableArray()},_=>recipe};
    private Slider AddSlider(string label,double min,double max,double initial,string unit,Action<double> update)
    {
        var row=new Grid{Margin=new(0,0,0,14)};row.ColumnDefinitions.Add(new(){Width=new(150)});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new(75)});
        row.Children.Add(Label(label));var slider=new Slider{Minimum=min,Maximum=max,Value=initial,VerticalAlignment=VerticalAlignment.Center,Margin=new(8,0,12,0)};Grid.SetColumn(slider,1);row.Children.Add(slider);var readout=Label(initial.ToString("0.##",CultureInfo.InvariantCulture)+" "+unit);readout.TextAlignment=TextAlignment.Right;Grid.SetColumn(readout,2);row.Children.Add(readout);slider.ValueChanged+=(_,_)=>{readout.Text=slider.Value.ToString("0.##",CultureInfo.InvariantCulture)+" "+unit;if(!hydratingControls)update(slider.Value);};body.Children.Add(row);return slider;
    }
    private static TextBlock Label(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,8,8)};
    private static TextBox Field(string text,string accessible){var result=new TextBox{Text=text,MinHeight=32,Padding=new(8,4,8,4),Margin=new(0,0,8,8)};System.Windows.Automation.AutomationProperties.SetName(result,accessible);return result;}
    private static Button Button(string text,Action action){var result=new Button{Content=text,MinHeight=30,Padding=new(12,5,12,5),Margin=new(0,0,8,8),HorizontalAlignment=HorizontalAlignment.Left};result.SetResourceReference(Control.StyleProperty,text=="−"?"TertiaryButton":"SecondaryButton");result.Click+=(_,_)=>action();return result;}
    private static string ToolTitle(string id,string fallback)=>id switch {"audio.trim"=>"Trim Audio","audio.channels"=>"Convert Audio Channels","audio.bleep"=>"Bleep Audio","audio.normalize"=>"Normalize Audio","audio.visualizer"=>"Audio Visualization","video.trim"=>"Trim Video","video.mute"=>"Mute Video","video.speed"=>"Video Speed","video.split"=>"Split Video","video.redact"=>"Redact Video","video.resize"=>"Resize Video",_=>fallback};
    public static string ActionLabel(string id)=>id switch {"audio.bleep"=>"Save Bleeped Copy","audio.compress" or "video.compress"=>"Compress","audio.metadata" or "video.metadata"=>"Save","video.snapshot"=>"Save Snapshot","video.split"=>"Split","audio.visualizer"=>"Save Visualization",_=>"Apply"};
    private static string Size(long bytes)=>bytes>=1024*1024?$"{bytes/(1024d*1024):0.##} MB":$"{bytes/1024d:0.##} KB";
    private static RationalTime Time(double value)=>MediaEditorEngine.Time(value);
    private static double Seconds(RationalTime value)=>MediaEffectFilters.Seconds(value);
    private void Run(Func<Task> command)=>_ = Track(command);
    private Task Track(Func<Task> command){Task task=Guard(command);work.Add(task);_=task.ContinueWith(_=>Dispatcher.BeginInvoke(new Action(()=>work.Remove(task))),TaskScheduler.Default);return task;}
    private async Task Guard(Func<Task> command)
    {
        try{await command();}
        catch(OperationCanceledException){}
        catch(Exception error)when(error is not OutOfMemoryException){if(!closed){status.Text=error.Message;UpdateAdmission();}}
    }
}
