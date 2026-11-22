using System.Reflection;
using System.Security.Cryptography;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using NodeLock.Core;
using StarMap.API;
using ScreenPoint = NodeLock.Core.ScreenPoint;

namespace NodeLock.Ksa;

// Version-pinned prototype; native preview/connection paths remain authoritative.
[StarMapMod]
public sealed partial class NodeLockMod
{
    public const string Owner = "nodelock.hover.5901";
    public const string Version = "0.1.0";
    private Harmony? harmony;
    private static bool enabled;
    private static VehicleEditor? editor;
    private static Part? carried;
    private static Part.Connector? hover;
    private static readonly Dictionary<Part.Connector,long> ids = new(ReferenceEqualityComparer.Instance);
    private static readonly List<Candidate> candidates = new();
    private static readonly Dictionary<long,Part.Connector> visible = new();
    private static readonly HashSet<Part> subtree = new(ReferenceEqualityComparer.Instance);
    private static long nextId;
    private static readonly PropertyInfo Looking = AccessTools.Property(typeof(VehicleEditor), "IsFreeCameraLooking");
    private static float2 marker;
    private static ScreenPoint[] outline = [];
    private static double3[]? ringVertices;
    private static string faultCondition="";
    private static string flashText="";
    private static long flashUntil;
    private static void FlashError(string text)
    {
        if(string.IsNullOrEmpty(text))return;
        flashText=text;
        flashUntil=Environment.TickCount64+2500;
    }

    [StarMapBeforeMain]
    public void Load()
    {
        if (harmony != null) return;
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(VehicleEditor).Assembly.Location)));
            if(hash != "170C28AA34845026195C89A23C0F3C241DB00EA4A8B86EF60EC097C32A7F8A39") throw new NotSupportedException("Requires inspected KSA 5901");
            if (Looking?.PropertyType != typeof(bool)) throw new MissingMemberException("IsFreeCameraLooking");
            var ring = AccessTools.Method(typeof(VehicleEditor), "DrawConnectorRing");
            if (ring == null || ring.GetParameters().Length != 6 || ring.GetParameters()[4].ParameterType != typeof(double4)) throw new MissingMethodException("DrawConnectorRing signature");
            harmony = new Harmony(Owner);
            ValidateBindings();
            harmony.PatchAll(typeof(NodeLockMod).Assembly);
            enabled = true;
            Console.WriteLine($"[NodeLock {Version}] Tab locking loaded. Single carried part; native symmetry and inheritance enabled.");
        }
        catch(Exception e) { Unload(); Console.WriteLine("[NodeLock] Disabled: "+e); }
    }

    [StarMapUnload]
    public void Unload() { enabled=false; ResetLock(); Clear(); ringVertices=null; harmony?.UnpatchAll(Owner); harmony=null; }
    private static void Clear() { editor=null; carried=null; hover=null; outline=[]; ids.Clear(); visible.Clear(); candidates.Clear(); subtree.Clear(); nextId=0; }
    private static bool Ordinary(Part.Connector c) => (c.Flags & (Part.Connector.Flag.ToSurface | Part.Connector.Flag.FromSurface | Part.Connector.Flag.Edge)) == 0 && c.Connection == null;

    private static void Update(VehicleEditor current, IViewport viewport)
    {
        var root=current.Highlighted;
        if (!ReferenceEquals(editor,current) || !ReferenceEquals(carried,root)) { if(lockedRoot!=null)InvalidateLock(); Clear(); editor=current; carried=root; }
        long? previous=hover != null && ids.TryGetValue(hover,out var old) ? old : null;
        hover=null; outline=[]; visible.Clear(); candidates.Clear(); subtree.Clear();
        if(root == null || !root.Grabbed || root.Tree == null || root.Tree.IsDisposed) {ResetLock();return;}
        if(current.IsUserTyping || ImGui.GetIO().WantCaptureMouse || ImGui.GetIO().WantCaptureKeyboard || KSA.Program.ConsoleWindow.IsOpen || GameSettings.Show || Popup.AnyOpen || (bool)Looking.GetValue(null)!) return;
        if (!ReferenceEquals(viewport,KSA.Program.InputViewport)) return;
        var cursor=ImGui.GetMousePos();
        bool Inside(float2 p) => p.X>=viewport.Position.X && p.Y>=viewport.Position.Y && p.X<viewport.Position.X+viewport.Size.X && p.Y<viewport.Position.Y+viewport.Size.Y;
        if(!Inside(cursor)) return;
        var iterator=new PartTreeChildrenIterator(root);
        subtree.Add(root);
        Part? child;
        while((child=iterator.GetNextNode())!=null) subtree.Add(child);
        var sources=root.Connectors.Where(Ordinary).ToArray();
        if(sources.Length==0)return;
        var camera=viewport.GetCamera();
        var matrix=current.EditingSpace.GetMatrixAsmb2Ego(camera);
        foreach(var part in current.EditingSpace.AllParts)
        {
            if(subtree.Contains(part) || part.Tree == null || part.Tree.IsDisposed) continue;
            foreach(var connector in part.Connectors)
            {
                if(!Ordinary(connector) || !sources.Any(s=>s.CanConnect(connector))) continue;
                var ego=connector.PositionEgo(in matrix);
                double depth=double3.Dot(ego,camera.GetForwardEcl());
                if(!double.IsFinite(depth) || depth<=0) continue;
                var screen=viewport.Position+camera.EgoToScreen(ego);
                if(!float.IsFinite(screen.X)||!float.IsFinite(screen.Y)||!Inside(screen)) continue;
                if(!ids.TryGetValue(connector,out var id)) ids.Add(connector,id=++nextId);
                visible[id]=connector;
                candidates.Add(new(id,(screen-cursor).Length(),depth));
            }
        }
        var selected=HoverPicker.Pick(candidates,previous,Math.Max(0.1,ImGuiHelper.InterfaceScale));
        if(lockedRoot!=null)hover=destination;
        else if(selected.HasValue)visible.TryGetValue(selected.Value,out hover);
        if(hover!=null)
        {
            marker=viewport.Position+camera.EgoToScreen(hover.PositionEgo(in matrix));
            ringVertices ??= ModLibrary.Get<MeshReference>("CircleMesh").PositionCompare.Distinct().ToArray();
            // Use precisely the same mesh/transform as DrawConnectorRing, then outline it
            // in the foreground so a green/depth-tested ring cannot cover the feedback.
            var transform=double4x4.CreateScale(hover.Scale)*double4x4.CreateFromQuaternion(hover.Asmb2Ego(current.EditingSpace.Asmb2Ecl))*double4x4.CreateTranslation(hover.PositionEgo(in matrix));
            var projected=new List<ScreenPoint>(ringVertices.Length);
            bool crossesCamera=false;
            foreach(var vertex in ringVertices)
            {
                var point=vertex.Transform(transform);
                if(double3.Dot(point,camera.GetForwardEcl())<=0){crossesCamera=true;break;}
                var screen=viewport.Position+camera.EgoToScreen(point);
                projected.Add(new(screen.X,screen.Y));
            }
            if(!crossesCamera)outline=Outline.Hull(projected);
        }
        // Bound retained connector references to the current eligible set.
        if(ids.Count>visible.Count+128)
            foreach(var key in ids.Keys.Where(k=>!visible.ContainsKey(ids[k])).ToArray()) ids.Remove(key);
    }

    [HarmonyPatch(typeof(VehicleEditor),nameof(VehicleEditor.OnPreRender))]
    private static class BeforeRender
    {
        static void Prefix(VehicleEditor __instance,IViewport viewport)
        {
            if(!enabled)return;
            try { Update(__instance,viewport); }
            catch(Exception e) { enabled=false; Clear(); Console.WriteLine("[NodeLock] Hover preview disabled: "+e); }
        }
    }
    [HarmonyPatch(typeof(VehicleEditor),"DrawConnectorRing")]
    private static class Ring
    {
        static void Prefix(VehicleEditor __instance,Part.Connector connector,ref double4 color)
        {
            if(enabled && !sideUnavailable && lockedRoot!=null && ReferenceEquals(connector,source))color=new double4(0,1,1,1);
            if(enabled && ReferenceEquals(editor,__instance) && ReferenceEquals(connector,hover)) color=new double4(1,0.8,0.05,1);
        }
    }
    [HarmonyPatch(typeof(VehicleEditor),nameof(VehicleEditor.OnDrawUi))]
    private static class Hint
    {
        static void Postfix(VehicleEditor __instance,IGameViewport inViewport)
        {
            if(!enabled || !ReferenceEquals(editor,__instance))return;
            var draw=ImGui.GetForegroundDrawList();
            var condition=recovery ? "Lock cleared - Tab to resume placement." : (lockedRoot!=null&&sideUnavailable ? placementProblem : "");
            if(condition!=faultCondition){faultCondition=condition;FlashError(condition);}
            if(Environment.TickCount64<flashUntil && !string.IsNullOrEmpty(flashText))
            {
                var size=ImGui.CalcTextSize(flashText);
                var center=inViewport.Position+new float2(inViewport.Size.X*.5f,inViewport.Size.Y*.5f);
                draw.AddText(center-size*.5f,0xff30dcff,flashText);
            }
            if(previewBuilt)presented=true;
            draw.PushClipRect(inViewport.Position,inViewport.Position+new float2(inViewport.Size.X,inViewport.Size.Y),true);
            if(hover!=null)
            {
                draw.AddCircle(marker,5*ImGuiHelper.InterfaceScale,0xff30dcff);
                for(int i=0;i<outline.Length;i++)
                {
                    var a=outline[i];var b=outline[(i+1)%outline.Length];
                    draw.AddLine(new float2((float)a.X,(float)a.Y),new float2((float)b.X,(float)b.Y),0xff30dcff,2*ImGuiHelper.InterfaceScale);
                }
            }
            if(hover!=null && lockedRoot!=null && !sideUnavailable)
                draw.AddCircle(marker,9*ImGuiHelper.InterfaceScale,0xffffff00);
            draw.PopClipRect();
        }
    }
    [HarmonyPatch(typeof(VehicleEditor),nameof(VehicleEditor.Dispose))]
    private static class DisposeEditor { static void Prefix(VehicleEditor __instance) { if(ReferenceEquals(editor,__instance)){ResetLock();Clear();} } }
}
