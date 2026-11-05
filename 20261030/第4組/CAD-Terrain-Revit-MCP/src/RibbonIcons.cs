using M=System.Windows.Media;
namespace CadTerrainKit { internal static class RibbonIcons {
static void Add(M.DrawingGroup group,string path,string fill,string stroke,double width){var g=M.Geometry.Parse(path); M.Brush f=fill==null?null:(M.Brush)new M.BrushConverter().ConvertFromString(fill); var pen=new M.Pen((M.Brush)new M.BrushConverter().ConvertFromString(stroke),width);pen.StartLineCap=M.PenLineCap.Round;pen.EndLineCap=M.PenLineCap.Round;pen.LineJoin=M.PenLineJoin.Round; group.Children.Add(new M.GeometryDrawing(f,pen,g));}
public static M.ImageSource Create(string name){var group=new M.DrawingGroup(); group.Children.Add(new M.GeometryDrawing(M.Brushes.Transparent,null,new M.RectangleGeometry(new System.Windows.Rect(0,0,32,32))));switch(name){
case "choose":
Add(group,"M3,10 Q3,7 6,7 L13,7 L16,10 L26,10 Q29,10 29,13 L29,25 Q29,28 26,28 L6,28 Q3,28 3,25 Z","#F3C96B","#755535",1.4);
Add(group,"M12,4 L22,4 L27,9 L27,23 L12,23 Z","#FFFDF4","#596B70",1.4);
Add(group,"M22,4 L22,9 L27,9",null,"#596B70",1.2);
Add(group,"M15,13 L23,13 M15,17 L21,17",null,"#80AEB0",1.5);
Add(group,"M3,15 L29,15 L27,26 Q27,28 24,28 L7,28 Q4,28 4,25 Z","#F7D881","#755535",1.4);
break;
case "inspect":
Add(group,"M5,3 L22,3 Q24,3 24,5 L24,26 Q24,28 22,28 L5,28 Q3,28 3,26 L3,5 Q3,3 5,3 Z","#FFFDF4","#596B70",1.4);
Add(group,"M7,8 L17,8 M7,12 L14,12 M7,16 L11,16",null,"#A6BBBA",1.5);
Add(group,"M27,19 A9,9 0 1 1 9,19 A9,9 0 1 1 27,19 Z","#D9F0E9","#427D76",1.7);
Add(group,"M14,19 L17,22 L22,16",null,"#427D76",2.2);
Add(group,"M25,25 L30,30",null,"#596B70",3);
break;
case "build":
Add(group,"M2,26 L10,10 Q11,8 12,10 L20,25 Z","#A9CE9A","#587F64",1.4);
Add(group,"M11,27 L20,13 Q21,11 22,13 L30,27 Z","#C9DDB0","#587F64",1.4);
Add(group,"M16,28 L16,18 L22,13 L28,18 L28,28 Z","#FFF2D1","#755535",1.4);
Add(group,"M14,19 L22,12 L30,19",null,"#C77B60",2.3);
Add(group,"M21,28 L21,23 L24,23 L24,28","#B3D9DC","#596B70",1.1);
Add(group,"M3,29 Q10,23 16,29",null,"#E6BD73",3);
break;
} var image=new M.DrawingImage(group);image.Freeze();return image;} } }