namespace NodeLock.Core;

public readonly record struct ScreenPoint(double X, double Y);
public static class Outline
{
    // Convex projected silhouette of the native ring mesh, independent of its vertex ordering.
    public static ScreenPoint[] Hull(IEnumerable<ScreenPoint> input)
    {
        var points=input.Where(p=>double.IsFinite(p.X)&&double.IsFinite(p.Y)).Distinct().OrderBy(p=>p.X).ThenBy(p=>p.Y).ToArray();
        if(points.Length<3)return points;
        static double Cross(ScreenPoint a,ScreenPoint b,ScreenPoint c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        var hull=new List<ScreenPoint>();
        foreach(var p in points){while(hull.Count>=2&&Cross(hull[^2],hull[^1],p)<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        int lower=hull.Count;
        for(int i=points.Length-2;i>=0;i--){var p=points[i];while(hull.Count>lower&&Cross(hull[^2],hull[^1],p)<=0)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        hull.RemoveAt(hull.Count-1);
        return hull.ToArray();
    }
}
