using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using Citrus.Core.Editing;

namespace Citrus.Windows.Editors.Image;

public sealed class MaskInspector : StackPanel
{
    private readonly List<Action> pending=[];
    public void FlushFields(){foreach(var commit in pending)commit();}
    public MaskInspector(PixelMask original,AnnotationOverlay overlay,Action refresh,Action<string> error,Action rebuild)
    {
        Children.Add(new TextBlock{Text=original.Effect==MaskEffect.Solid?"Solid mask":original.Effect==MaskEffect.Gaussian?"Blur mask":"Pixelate mask",TextWrapping=TextWrapping.Wrap,FontWeight=FontWeights.SemiBold,Margin=new(0,8,0,6)});
        Children.Add(new TextBlock{Text=original.Effect==MaskEffect.Solid?"Replaces covered pixels in the saved image.":"Blur and pixelation only obscure details. Use Solid to remove covered pixels.",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,10)});
        var effect=new ComboBox{ItemsSource=Enum.GetValues<MaskEffect>(),SelectedItem=original.Effect};AutomationProperties.SetName(effect,"Manual mask effect");Children.Add(effect);
        effect.SelectionChanged+=(_,_)=>{Apply(m=>m with {Effect=(MaskEffect)effect.SelectedItem,Radius=(MaskEffect)effect.SelectedItem==MaskEffect.Solid?0:Math.Max(1,m.Radius)});rebuild();};
        Field("Radius · normalized source pixels",original.Radius,value=>Apply(m=>m with {Radius=value}));
        var color=new ComboBox{ItemsSource=new[]{"Black","White","Citrus orange"},SelectedIndex=original.Fill.R==255?(original.Fill.G==255?1:2):0,Margin=new(0,8,0,4)};AutomationProperties.SetName(color,"Solid mask color");Children.Add(color);
        color.SelectionChanged+=(_,_)=>Apply(m=>m with {Fill=color.SelectedIndex switch{1=>new(255,255,255,255),2=>new(255,107,22,255),_=>new(0,0,0,255)}});
        var bounds=overlay.SelectedBounds()!.Value;
        foreach(var(name,value)in new[]{("X",bounds.X),("Y",bounds.Y),("Width",bounds.Width),("Height",bounds.Height)})Field(name+" · px",value,v=>{var b=overlay.SelectedBounds()!.Value;b=name switch{"X"=>b with{X=v},"Y"=>b with{Y=v},"Width"=>b with{Width=v},_=>b with{Height=v}};overlay.SetBounds(b);refresh();},name is "Width" or "Height"?1:-16384,16384);
        var ordering=new WrapPanel{Margin=new(0,12,0,4)};
        foreach(var (label,delta)in new[]{("Back",-1),("Forward",1)}){var button=new Button{Content=label,Margin=new(0,0,5,5)};button.SetResourceReference(StyleProperty,"SecondaryButton");button.Click+=(_,_)=>{overlay.Reorder(delta);refresh();};ordering.Children.Add(button);}Children.Add(ordering);
        var delete=new Button{Content="Delete mask",Margin=new(0,12,0,8)};delete.SetResourceReference(StyleProperty,"SecondaryButton");delete.Click+=(_,_)=>{overlay.Delete();refresh();};Children.Add(delete);
        void Apply(Func<PixelMask,PixelMask> update)
        {
            try{var mask=((ImageRecipe)overlay.Document!.Recipe).Masks.Single(m=>m.Id==original.Id);overlay.ReplaceMask(update(mask));error("");refresh();}catch(ArgumentException e){error(e.Message);}
        }
        void Field(string label,double initial,Action<double> apply,double min=0,double max=100)
        {
            Children.Add(new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap,Margin=new(0,9,0,4)});var text=new TextBox{Text=initial.ToString("0.###",CultureInfo.InvariantCulture),Padding=new(8)};AutomationProperties.SetName(text,label);Children.Add(text);string accepted=text.Text;
            void Commit(){if(text.Text==accepted)return;if(!double.TryParse(text.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value)||value<min||value>max)throw new ArgumentException($"{label}: enter a value from {min} to {max}.");apply(value);accepted=text.Text;}
            pending.Add(Commit);
            void TryCommit(){try{Commit();error("");}catch(ArgumentException e){error(e.Message);}}
            text.LostKeyboardFocus+=(_,_)=>TryCommit();text.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Enter){TryCommit();e.Handled=true;}};
        }
    }
}
