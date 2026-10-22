using System.Collections.Immutable;
using Citrus.Core.Editing;
using Tabula;
using Tabula.Detectors;
using Tabula.Extractors;
using Tesseract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace Citrus.Conversion.Editing.Pdf;

public sealed record PdfTextObject(string Text, RectD Bounds, double FontSize, bool Bold);
public sealed record PdfImageObject(byte[] Png, RectD Bounds);
public sealed record PdfTableObject(ImmutableArray<ImmutableArray<string>> Rows, RectD Bounds);
public sealed record PdfSemanticPage(int Number, double Width, double Height, ImmutableArray<PdfTextObject> Text, ImmutableArray<PdfImageObject> Images, ImmutableArray<PdfTableObject> Tables, bool Ocr);
public sealed record PdfSemanticDocument(ImmutableArray<PdfSemanticPage> Pages, ImmutableArray<string> Warnings);

/// <summary>One coordinate-bearing model shared by all editable PDF exporters.</summary>
public static class PdfSemanticExtractor
{
    public static Task<PdfSemanticDocument> ExtractAsync(string path, IReadOnlyList<int> selected, string featurePack, CancellationToken ct, bool recoverArtwork = true) => Task.Run(async () =>
    {
        using var input = PdfDocument.Open(path, new ParsingOptions { ClipPaths = true });
        if (input.IsEncrypted) throw new NotSupportedException("Encrypted PDFs are not supported.");
        if(selected.Count is <1 or >10000)throw new NotSupportedException("Select 1–10,000 PDF pages.");
        var pages = ImmutableArray.CreateBuilder<PdfSemanticPage>(); var warnings = ImmutableArray.CreateBuilder<string>();
        long imageBytes=0;int textObjects=0;
        foreach (int number in selected)
        {
            ct.ThrowIfCancellationRequested(); var page = input.GetPage(number);
            if(page.Letters.Count>200_000||page.Paths.Count>100_000)throw new NotSupportedException($"Page {number} exceeds the editable reconstruction complexity limit.");
            var tables = ImmutableArray.CreateBuilder<PdfTableObject>();
            var area = ObjectExtractor.Extract(input, number);
            var extracted = new SpreadsheetExtractionAlgorithm().Extract(area);
            if (extracted.Count == 0)
            {
                var regions = new SimpleNurminenDetectionAlgorithm().Detect(area);
                extracted = regions.SelectMany(region => new BasicExtractionAlgorithm().Extract(area.GetArea(region.BoundingBox))).ToArray();
            }
            foreach (var table in extracted)
            {
                var rows = table.Rows.Select(row => row.Select(cell => cell.GetText().Trim()).ToImmutableArray()).ToImmutableArray();
                if (rows.Length < 2 || rows.Max(row => row.Length) < 2 || rows.Count(row => row.Count(s => s.Length > 0) >= 2) < 2) continue;
                tables.Add(new(rows, new(table.Left, page.Height - table.Top, table.Width, table.Height)));
            }
            var text = ImmutableArray.CreateBuilder<PdfTextObject>();
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToArray();
            var embeddedImages=page.GetImages().ToArray();if(embeddedImages.Length>4096)throw new NotSupportedException("A page exceeds the embedded-image reconstruction limit.");
            var imageRegions=embeddedImages.Select(image=>new RectD(image.BoundingBox.Left,page.Height-image.BoundingBox.Top,image.BoundingBox.Width,image.BoundingBox.Height)).ToArray();
            var scannedRegions=imageRegions.Where(region=>region.Width*region.Height>=page.Width*page.Height*.03).ToArray();
            if (words.Length > 0)
            {
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                foreach (var block in UnsupervisedReadingOrderDetector.Instance.Get(blocks).OrderBy(b => b.ReadingOrder))
                    foreach (var line in block.TextLines)
                    {
                        var box = line.BoundingBox;
                        var bounds = new RectD(box.Left, page.Height - box.Top, box.Width, box.Height);
                        if (tables.Any(table => Contains(table.Bounds, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))) continue;
                        var letters = line.Words.SelectMany(w => w.Letters).ToArray();
                        text.Add(new(line.Text, bounds, Math.Clamp(letters.Select(l => l.FontSize).DefaultIfEmpty(12).Average(), 4, 144), letters.Any(l => l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true)));
                    }
            }
            bool ocr = words.Length == 0||scannedRegions.Length>0;
            var reconstructedRegions=new List<RectD>();
            if (ocr)
            {
                string data = Path.Combine(featurePack,"tessdata");
                if (!File.Exists(Path.Combine(data,"eng.traineddata"))) throw new NotSupportedException("The bundled English OCR data is missing. Repair the offline feature pack.");
                // A scan already has a finite pixel resolution. Rendering its
                // 96-DPI PDF wrapper at300DPI needlessly triples glyph size and
                // can reduce digit recognition. Never upscale beyond the scan's
                // effective resolution; this does not change exported page DPI.
                var scanDpis=embeddedImages.Where(image=>image.BoundingBox.Width>0&&image.BoundingBox.Height>0&&image.BoundingBox.Width*image.BoundingBox.Height>=page.Width*page.Height*.03)
                    .Select(image=>Math.Min(image.WidthInSamples*72d/image.BoundingBox.Width,image.HeightInSamples*72d/image.BoundingBox.Height)).Where(double.IsFinite).ToArray();
                int ocrDpi=scanDpis.Length==0?300:(int)Math.Clamp(Math.Round(scanDpis.Min()),72,300);
                byte[] png = await PdfEngine.RenderPageAsync(path, number, ocrDpi, ct).ConfigureAwait(false);
                using var pix = Pix.LoadFromMemory(png); using var engine = new TesseractEngine(data,"eng",EngineMode.LstmOnly);
                var recognizedWords = new List<(string Text, Tesseract.Rect Box, float Confidence)>();
                using (var recognized = engine.Process(pix)) using (var iterator = recognized.GetIterator())
                {
                    iterator.Begin();
                    do
                    {
                        ct.ThrowIfCancellationRequested(); string? value = iterator.GetText(PageIteratorLevel.Word);
                        if (!string.IsNullOrWhiteSpace(value) && iterator.TryGetBoundingBox(PageIteratorLevel.Word,out var box))
                        {
                            double x=(box.X1+box.X2)*page.Width/(2*pix.Width),y=(box.Y1+box.Y2)*page.Height/(2*pix.Height);
                            if(words.Length==0||scannedRegions.Any(region=>Contains(region,x,y))&&!text.Any(native=>Contains(native.Bounds,x,y))&&!tables.Any(table=>Contains(table.Bounds,x,y)))
                                recognizedWords.Add((value.Trim(),box,iterator.GetConfidence(PageIteratorLevel.Word)));
                        }
                    } while (iterator.Next(PageIteratorLevel.Word));
                }
                var scannedTables=FindScannedTables(png,recognizedWords,page.Width,page.Height,engine,pix,ct).Where(table=>
                    !tables.Any(native=>Contains(native.Bounds,table.Bounds.X+table.Bounds.Width/2,table.Bounds.Y+table.Bounds.Height/2))&&
                    (words.Length==0||scannedRegions.Any(region=>Contains(region,table.Bounds.X+table.Bounds.Width/2,table.Bounds.Y+table.Bounds.Height/2)))).ToImmutableArray();
                tables.AddRange(scannedTables);
                foreach (var line in recognizedWords.GroupBy(word => Math.Round(word.Box.Y1 / 12d)).OrderBy(g => g.Key))
                {
                    var ordered = line.OrderBy(word => word.Box.X1).ToArray();
                    double left = ordered.Min(w => w.Box.X1), top = ordered.Min(w => w.Box.Y1), right = ordered.Max(w => w.Box.X2), bottom = ordered.Max(w => w.Box.Y2);
                    var bounds=new RectD(left * page.Width / pix.Width,top * page.Height / pix.Height,(right-left)*page.Width/pix.Width,(bottom-top)*page.Height/pix.Height);
                    if(!tables.Any(table=>Contains(table.Bounds,bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2)))text.Add(new(string.Join(' ',ordered.Select(w => w.Text)),bounds,Math.Clamp((bottom-top)*72/ocrDpi,4,144),false));
                }
                if(recognizedWords.Count>0||!scannedTables.IsEmpty)warnings.Add($"Page {number}: text reconstructed by English OCR; reading order and formatting require review.");
                if (recognizedWords.Any(w => w.Confidence < 70)) warnings.Add($"Page {number}: some OCR words have low confidence.");
                if (recognizedWords.Count == 0&&scannedTables.IsEmpty) warnings.Add($"Page {number}: no additional readable text was recovered from its raster regions.");
                foreach(var region in scannedRegions)
                {
                    var inRegion=recognizedWords.Where(word=>Contains(region,(word.Box.X1+word.Box.X2)*page.Width/(2*pix.Width),(word.Box.Y1+word.Box.Y2)*page.Height/(2*pix.Height))).ToArray();
                    if(inRegion.Length>=3&&inRegion.Average(word=>word.Confidence)>=60||scannedTables.Any(table=>Contains(region,table.Bounds.X+table.Bounds.Width/2,table.Bounds.Y+table.Bounds.Height/2)))reconstructedRegions.Add(region);
                }
                if(words.Length>0&&reconstructedRegions.Count>0)warnings.Add($"Page {number}: a raster text region was replaced with editable OCR; artwork inside that scanned region may require manual restoration.");
            }
            var images = ImmutableArray.CreateBuilder<PdfImageObject>();
            if (recoverArtwork) foreach (var image in embeddedImages)
            {
                ct.ThrowIfCancellationRequested(); var box = image.BoundingBox;
                var bounds=new RectD(box.Left,page.Height-box.Top,box.Width,box.Height);
                if(reconstructedRegions.Contains(bounds))continue;
                if((long)image.WidthInSamples*image.HeightInSamples>32_000_000)throw new NotSupportedException("An embedded PDF image exceeds the 32-megapixel reconstruction limit.");
                if (image.TryGetPng(out var png) && png.Length <= 64 * 1024 * 1024)
                { imageBytes=checked(imageBytes+png.Length);if(imageBytes>256L*1024*1024)throw new NotSupportedException("Recovered artwork exceeds the 256-MiB reconstruction budget.");images.Add(new(png,new(box.Left,page.Height-box.Top,box.Width,box.Height))); }
                else warnings.Add($"Page {number}: an embedded image could not be recovered.");
            }
            textObjects=checked(textObjects+text.Count);if(textObjects>500_000)throw new NotSupportedException("The document exceeds the editable text-object budget.");
            pages.Add(new(number,page.Width,page.Height,text.ToImmutable(),images.ToImmutable(),tables.ToImmutable(),ocr));
        }
        warnings.Add("PDF reconstruction is best effort; verify fonts, reading order, artwork and tables before using the copy.");
        return new PdfSemanticDocument(pages.ToImmutable(),warnings.Distinct().ToImmutableArray());
    },ct);
    private static bool Contains(RectD box,double x,double y) => x >= box.X && x <= box.X+box.Width && y >= box.Y && y <= box.Y+box.Height;
    private static ImmutableArray<PdfTableObject> FindScannedTables(byte[] png,List<(string Text,Tesseract.Rect Box,float Confidence)> words,double pageWidth,double pageHeight,TesseractEngine engine,Pix pix,CancellationToken ct)
    {
        using var image=new ImageMagick.MagickImage(png);using var pixels=image.GetPixels();byte[] rgb=pixels.ToByteArray(ImageMagick.PixelMapping.RGB)!;
        int width=(int)image.Width,height=(int)image.Height;
        bool Dark(int x,int y){int i=(y*width+x)*3;return rgb[i]<100&&rgb[i+1]<100&&rgb[i+2]<100;}
        var horizontal=new List<(int Axis,int Start,int End)>();var vertical=new List<(int Axis,int Start,int End)>();
        for(int y=0;y<height;y++)
        {
            ct.ThrowIfCancellationRequested();int start=-1,bestStart=0,bestEnd=0;
            for(int x=0;x<=width;x++)if(x<width&&Dark(x,y)){if(start<0)start=x;}else if(start>=0){if(x-start>bestEnd-bestStart){bestStart=start;bestEnd=x;}start=-1;}
            if(bestEnd-bestStart>=width*.25)horizontal.Add((y,bestStart,bestEnd));
        }
        for(int x=0;x<width;x++)
        {
            ct.ThrowIfCancellationRequested();int start=-1,bestStart=0,bestEnd=0;
            for(int y=0;y<=height;y++)if(y<height&&Dark(x,y)){if(start<0)start=y;}else if(start>=0){if(y-start>bestEnd-bestStart){bestStart=start;bestEnd=y;}start=-1;}
            if(bestEnd-bestStart>=height*.15)vertical.Add((x,bestStart,bestEnd));
        }
        horizontal=Merge(horizontal);vertical=Merge(vertical);
        var result=ImmutableArray.CreateBuilder<PdfTableObject>();var used=new HashSet<int>();
        for(int first=0;first<horizontal.Count;first++)
        {
            if(used.Contains(first))continue;var hs=new HashSet<int>{first};var vs=new HashSet<int>();bool changed;
            do
            {
                changed=false;
                for(int v=0;v<vertical.Count;v++)if(!vs.Contains(v)&&hs.Any(h=>Intersects(horizontal[h],vertical[v]))) {vs.Add(v);changed=true;}
                for(int h=0;h<horizontal.Count;h++)if(!hs.Contains(h)&&vs.Any(v=>Intersects(horizontal[h],vertical[v]))) {hs.Add(h);changed=true;}
            }while(changed);
            foreach(int h in hs)used.Add(h);
            if(hs.Count<3||vs.Count<2)continue;var ys=hs.Select(i=>horizontal[i].Axis).Order().ToArray();var xs=vs.Select(i=>vertical[i].Axis).Order().ToArray();
            if(xs.Length>200||ys.Length>10000||(long)(xs.Length-1)*(ys.Length-1)>100_000)throw new NotSupportedException("The scanned table exceeds supported cell limits.");
            var assigned=new Dictionary<(int Row,int Column),List<(string Text,Tesseract.Rect Box,float Confidence)>>();
            foreach(var word in words)
            {
                int column=Array.BinarySearch(xs,(word.Box.X1+word.Box.X2)/2),row=Array.BinarySearch(ys,(word.Box.Y1+word.Box.Y2)/2);
                column=column>=0?column:~column-1;row=row>=0?row:~row-1;
                if(column<0||row<0||column>=xs.Length-1||row>=ys.Length-1)continue;
                if(!assigned.TryGetValue((row,column),out var content))assigned[(row,column)]=content=[];content.Add(word);
            }
            var rows=ImmutableArray.CreateBuilder<ImmutableArray<string>>();
            for(int row=0;row<ys.Length-1;row++)
            {
                var cells=ImmutableArray.CreateBuilder<string>();
                for(int col=0;col<xs.Length-1;col++)
                {
                    var contents=assigned.GetValueOrDefault((row,col),[]).OrderBy(w=>Math.Round(w.Box.Y1/12d)).ThenBy(w=>w.Box.X1);
                    string value=string.Join(' ',contents.Select(w=>w.Text));
                    if(value.Length==0)
                    {
                        ct.ThrowIfCancellationRequested();int inset=Math.Clamp((int)Math.Round(width/pageWidth*2),2,16),left=xs[col]+inset,top=ys[row]+inset,cellWidth=xs[col+1]-xs[col]-2*inset,cellHeight=ys[row+1]-ys[row]-2*inset;
                        if(cellWidth>4&&cellHeight>4)
                        {
                            using var cell=engine.Process(pix,new Tesseract.Rect(left,top,cellWidth,cellHeight),PageSegMode.SingleBlock);
                            value=cell.GetText().Trim();
                        }
                    }
                    cells.Add(value);
                }
                rows.Add(cells.ToImmutable());
            }
            if(rows.Count(r=>r.Count(c=>c.Length>0)>=2)<2)continue;
            result.Add(new(rows.ToImmutable(),new(xs[0]*pageWidth/width,ys[0]*pageHeight/height,(xs[^1]-xs[0])*pageWidth/width,(ys[^1]-ys[0])*pageHeight/height)));
        }
        return result.ToImmutable();
        static bool Intersects((int Axis,int Start,int End) h,(int Axis,int Start,int End) v)=>v.Axis>=h.Start-3&&v.Axis<=h.End+3&&h.Axis>=v.Start-3&&h.Axis<=v.End+3;
        static List<(int Axis,int Start,int End)> Merge(List<(int Axis,int Start,int End)> lines)
        {
            var merged=new List<(int,int,int)>();int i=0;
            while(i<lines.Count){int start=i,end=i;while(end+1<lines.Count&&lines[end+1].Axis-lines[end].Axis<=2)end++;merged.Add((lines[(start+end)/2].Axis,lines.Skip(start).Take(end-start+1).Min(l=>l.Start),lines.Skip(start).Take(end-start+1).Max(l=>l.End)));i=end+1;}
            return merged;
        }
    }
}
