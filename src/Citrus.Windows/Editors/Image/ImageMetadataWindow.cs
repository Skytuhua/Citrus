using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using Citrus.Conversion.Editing;
using Citrus.Conversion.Editing.Image;
using Citrus.Core.Editing;
using Citrus.Windows.Presentation;
using Microsoft.Win32;

namespace Citrus.Windows.Editors.Image;

public sealed class ImageMetadataWindow:Window
{
    private sealed class SourceState(string path){public string Path{get;}=path;public SourceSnapshot? Source;public ImmutableArray<MetadataField> Fields=[];public Dictionary<(string Scope,string Key),MetadataEdit> Edits=[];public Dictionary<(string Scope,string Key),string> Raw=[];public HashSet<(string Scope,string Key)> Invalid=[];public bool RemoveAll,Included=true;public string? Error;public override string ToString()=>System.IO.Path.GetFileName(Path);}
    private readonly ExifToolMetadataService metadata;private readonly ISourceSnapshotService sources;
    private readonly CancellationTokenSource lifetime=new();private readonly List<SourceState> inputs=[];
    private readonly ComboBox sourcePicker=new(){MinWidth=160};private readonly TextBox search=new(){MinWidth=180,ToolTip="Search metadata"};
    private readonly StackPanel rows=new();private readonly TextBlock message=new(){TextWrapping=TextWrapping.Wrap};
    private readonly Button save=new(){Content="Save",IsEnabled=false};private readonly Button removeAll=new(){Content="Remove All",IsEnabled=false};
    private readonly CheckBox included=new(){Content="Include this file",IsChecked=true};
    private EditorOpenRequest request=null!;private EditorSubmissionBatch? accepted;private string? destination;private bool loading,closed,shown,dirty;
    private Task loadingTask=Task.CompletedTask;
    private SourceState? Selected=>sourcePicker.SelectedItem as SourceState;
    public ImageMetadataWindow(ImageMetadataEngine engine):this(engine.Metadata,engine.Sources){}
    public ImageMetadataWindow(ExifToolMetadataService metadata,ISourceSnapshotService? sources=null)
    {
        this.metadata=metadata;this.sources=sources??new SourceSnapshotService();Title="Metadata · Citrus";Width=600;Height=600;MinWidth=440;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(FontFamilyProperty,"AppFont");SetResourceReference(FontSizeProperty,"BodySize");SetResourceReference(ForegroundProperty,"PrimaryText");
        var shell=new MaterialSurface();var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});shell.Child=grid;Content=shell;
        var heading=new TextBlock{Text="Metadata",FontSize=22,FontWeight=FontWeights.SemiBold};grid.Children.Add(new ChromeHeader{Content=heading});
        var top=new Grid{Margin=new(24,16,24,12)};top.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});top.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});top.RowDefinitions.Add(new(){Height=GridLength.Auto});top.RowDefinitions.Add(new(){Height=GridLength.Auto});
        sourcePicker.Margin=new(0,0,16,8);sourcePicker.VerticalAlignment=VerticalAlignment.Bottom;AutomationProperties.SetName(sourcePicker,"Metadata file");top.Children.Add(sourcePicker);
        var searchField=new StackPanel();Grid.SetColumn(searchField,1);searchField.Children.Add(new TextBlock{Text="Search metadata",Margin=new(0,0,0,4)});search.Margin=new(0,0,0,8);AutomationProperties.SetName(search,"Search metadata");searchField.Children.Add(search);top.Children.Add(searchField);Grid.SetRow(included,1);top.Children.Add(included);Grid.SetRow(top,1);grid.Children.Add(top);
        var scroll=new ScrollViewer{Content=rows,Margin=new(24,0,24,0),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,2);grid.Children.Add(scroll);
        var footer=new StackPanel{Margin=new(24,16,24,20)};var warning=new TextBlock{Text="Removing metadata may change HDR or portrait effects.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,10),FontSize=12};warning.SetResourceReference(TextBlock.ForegroundProperty,"SecondaryText");footer.Children.Add(warning);message.SetResourceReference(TextBlock.ForegroundProperty,"ErrorText");AutomationProperties.SetLiveSetting(message,AutomationLiveSetting.Assertive);footer.Children.Add(message);
        var buttons=new DockPanel{Margin=new(0,12,0,0)};var folder=new Button{Content="Save location…",HorizontalAlignment=HorizontalAlignment.Left};folder.SetResourceReference(StyleProperty,"SecondaryButton");buttons.Children.Add(folder);var right=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};removeAll.SetResourceReference(StyleProperty,"SecondaryButton");removeAll.Margin=new(0,0,8,0);right.Children.Add(removeAll);right.Children.Add(save);buttons.Children.Add(right);footer.Children.Add(buttons);
        var footerBorder=new Border{BorderThickness=new(0,1,0,0),Child=footer};footerBorder.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");Grid.SetRow(footerBorder,3);grid.Children.Add(footerBorder);
        sourcePicker.SelectionChanged+=(_,_)=>Rebuild();search.TextChanged+=(_,_)=>Rebuild();included.Click+=(_,_)=>{if(Selected is {} state){state.Included=included.IsChecked==true;dirty=true;UpdateSave();}};
        removeAll.Click+=(_,_)=>{if(Selected is {} state){state.RemoveAll=true;state.Edits.Clear();state.Raw.Clear();state.Invalid.Clear();dirty=true;Rebuild();}};
        save.SetResourceReference(StyleProperty,"PrimaryButton");save.Click+=(_,_)=>Accept();folder.Click+=(_,_)=>{var picker=new OpenFolderDialog{Title="Save metadata copies to"};if(picker.ShowDialog(this)==true){destination=picker.FolderName;dirty=true;}};
        PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape){Close();e.Handled=true;}};
        Closing+=CheckClose;Closed+=(_,_)=>{closed=true;lifetime.Cancel();};WindowChromeSetup.Apply(this);(Application.Current as App)?.AttachPresentation(this,shell);
    }
    public async Task<EditorSubmissionBatch?> ShowEditorAsync(EditorOpenRequest open,CancellationToken ct)
    {
        if(shown)throw new InvalidOperationException("An editor window can be shown once.");shown=true;request=open;destination=open.DestinationDirectory;
        Loaded+=Load;using var cancellation=ct.Register(()=>Dispatcher.BeginInvoke(new Action(()=>{if(closed)return;lifetime.Cancel();Close();})));
        try{ct.ThrowIfCancellationRequested();ShowDialog();return accepted;}finally{lifetime.Cancel();await loadingTask;lifetime.Dispose();}
        void Load(object sender,RoutedEventArgs e){Loaded-=Load;loadingTask=LoadSources();}
        async Task LoadSources()
        {
            loading=true;message.Text="Reading metadata…";
            foreach(string path in open.InitialPaths)
            {
                var state=new SourceState(path);inputs.Add(state);
                try{var capture=(await sources.CaptureAsync([path],lifetime.Token)).Single();state.Source=capture.Snapshot??throw new IOException(capture.Error);state.Fields=await metadata.ReadAsync(state.Source,lifetime.Token);}
                catch(OperationCanceledException)when(lifetime.IsCancellationRequested){return;}
                catch(Exception error)when(error is not OutOfMemoryException){state.Error=error.Message;}
            }
            if(closed)return;loading=false;sourcePicker.ItemsSource=inputs;sourcePicker.SelectedIndex=0;message.Text="";Rebuild();
        }
    }
    private void Rebuild()
    {
        rows.Children.Clear();if(Selected is not {} state)return;included.IsChecked=state.Included;
        if(state.Error is {} error){rows.Children.Add(new TextBlock{Text=error+" Exclude this file to save the others.",TextWrapping=TextWrapping.Wrap});UpdateSave();return;}
        string query=search.Text.Trim();var fields=state.Fields.Where(f=>query.Length==0||($"{f.Scope} {f.DisplayName} {ExifToolMetadataService.Text(f.Value)}").Contains(query,StringComparison.OrdinalIgnoreCase)).GroupBy(f=>f.Scope);
        foreach(var group in fields)
        {
            var heading=new TextBlock{Text=group.Key,FontWeight=FontWeights.SemiBold,Margin=new(0,16,0,8)};heading.SetResourceReference(TextBlock.ForegroundProperty,"SecondaryText");rows.Children.Add(heading);
            foreach(var field in group)
            {
                var key=(field.Scope,field.Key);state.Edits.TryGetValue(key,out var edit);bool removed=state.RemoveAll&&field.Removable||edit?.Action==MetadataEditAction.Remove;
                var row=new Grid{Margin=new(0,4,0,8)};row.ColumnDefinitions.Add(new(){Width=new(2,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new(3,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new(34)});
                row.Children.Add(new TextBlock{Text=field.DisplayName+":",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,12,0)});
                var box=new TextBox{Text=removed?"Removed":state.Raw.GetValueOrDefault(key)??ExifToolMetadataService.Text(edit?.Value??field.Value),IsReadOnly=!field.Writable||removed,Opacity=removed?.5:1};AutomationProperties.SetName(box,field.DisplayName);Grid.SetColumn(box,1);
                if(field.Writable)row.Children.Add(box);
                else {var readOnly=new TextBlock{Text=box.Text,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,Margin=new(12,8,12,8),ToolTip="Read only"};readOnly.SetResourceReference(TextBlock.ForegroundProperty,"SecondaryText");AutomationProperties.SetName(readOnly,field.DisplayName+" (read only)");Grid.SetColumn(readOnly,1);row.Children.Add(readOnly);}
                var remove=new Button{Content=removed?"↶":"−",IsEnabled=field.Removable&&!state.RemoveAll,ToolTip=removed?"Restore field":"Remove field",Margin=new(4,0,0,0)};remove.SetResourceReference(StyleProperty,"TertiaryButton");AutomationProperties.SetName(remove,(removed?"Restore ":"Remove ")+field.DisplayName);Grid.SetColumn(remove,2);row.Children.Add(remove);rows.Children.Add(row);
                box.TextChanged+=(_,_)=>{if(box.IsReadOnly)return;state.Raw[key]=box.Text;try{MetadataValue value=field.Value switch{IntegerValue=>new IntegerValue(long.Parse(box.Text,CultureInfo.InvariantCulture)),RealValue=>new RealValue(double.Parse(box.Text,CultureInfo.InvariantCulture)),_=>new TextValue(box.Text)};state.Edits[key]=new(field.Scope,field.Key,MetadataEditAction.Set,value);state.Invalid.Remove(key);var errors=metadata.ValidateEdits(state.Fields,[..state.Edits.Values]);message.Text=string.Join(" ",errors.Select(i=>i.Message));dirty=true;UpdateSave();}catch(Exception e)when(e is FormatException or OverflowException){state.Invalid.Add(key);message.Text="Enter a valid number for "+field.DisplayName;save.IsEnabled=false;}};
                remove.Click+=(_,_)=>{state.Raw.Remove(key);state.Invalid.Remove(key);if(removed)state.Edits.Remove(key);else state.Edits[key]=new(field.Scope,field.Key,MetadataEditAction.Remove,null);dirty=true;Rebuild();};
            }
        }
        if(rows.Children.Count==0)rows.Children.Add(new TextBlock{Text="No matching metadata.",Margin=new(0,24,0,24)});
        removeAll.IsEnabled=state.Fields.Any(f=>f.Removable)&&!state.RemoveAll;UpdateSave();
    }
    private void UpdateSave()=>save.IsEnabled=!loading&&inputs.Any(s=>s.Included)&&inputs.Where(s=>s.Included).All(s=>s.Source is not null&&s.Error is null&&s.Invalid.Count==0&&metadata.ValidateEdits(s.Fields,[..s.Edits.Values]).IsEmpty)&&inputs.Any(s=>s.Included&&(s.RemoveAll||s.Edits.Count>0));
    private void Accept()
    {
        try
        {
            if(!save.IsEnabled)return;var selected=inputs.Where(s=>s.Included).ToArray();var items=ImmutableArray.CreateBuilder<EditorSubmissionItem>();
            foreach(var state in selected)
            {
                var source=state.Source!;string format=Path.GetExtension(source.Path)[1..].ToLowerInvariant();
                var doc=new EditorDocument(1,0,[source],[],new ImageRecipe(source.Id,new(0,false,false,null,null,false),new(1,0,0,0,1,0,0,1,0,0,0,0,0,0,0,0,0,0,0,1,PhotoEffect.None,0),[],null,[],[..state.Edits.Values]),new(format,destination,Path.GetFileNameWithoutExtension(source.Path),new QualityGoal(85),state.RemoveAll?MetadataPolicy.StripDescriptive:MetadataPolicy.PreserveSupported,new(255,255,255,255)));
                items.Add(new(Guid.NewGuid(),[source.Id],doc));
            }
            accepted=new(request,[..inputs.Where(s=>s.Source is not null).Select(s=>s.Source!)],[..inputs.Where(s=>!s.Included).Select(s=>s.Path)],[],[..selected.Select(s=>s.Source!.Id)],items.ToImmutable());DialogResult=true;
        }
        catch(Exception error)when(error is ArgumentException or IOException){message.Text=error.Message;}
    }
    private void CheckClose(object? sender,CancelEventArgs e)
    {
        if(accepted is not null||!dirty||lifetime.IsCancellationRequested)return;
        e.Cancel=MessageBox.Show(this,"Discard metadata edits?","Keep editing?",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes;
    }
}
