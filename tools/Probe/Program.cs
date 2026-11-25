using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using HarmonyLib;
using NodeLock.Core;

var game = args.Length > 0 ? args[0] : "G:/Program Files/Ahwoo/Games/Kitten Space Agency";
var loader = args.Length > 2 ? args[2] : Path.GetFullPath(".tools/starmap-0.5.4/release");
var modDirectory = args.Length > 1 ? Path.GetDirectoryName(Path.GetFullPath(args[1]))! : Path.GetFullPath("nodelock/src/NodeLock.Ksa/bin/Release/net10.0");
AssemblyLoadContext.Default.Resolving += (_, n) =>
{
    foreach(var dir in new[]{game,loader,modDirectory})
        if(File.Exists(Path.Combine(dir,n.Name+".dll"))) return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dir,n.Name+".dll"));
    return null;
};
var path=Path.Combine(game,"KSA.dll");
if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != "FA741419E287FB7F673FBA3212BE622B5FC27DB91FA4A6632828FB9DA7B15C7E") throw new Exception("Uninspected KSA build");
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
var editor=assembly.GetType("KSA.VehicleEditor",true)!;
foreach(var instruction in PatchProcessor.GetOriginalInstructions(AccessTools.Method(assembly.GetType("KSA.ImGuiBackendGlfwImpl",true)!,"OnKey")))
    if(instruction.operand is MethodInfo method && method.Name.Contains("KeyEvent"))Console.WriteLine("Input call: "+method.DeclaringType+" "+method);
var resolve=AccessTools.Method(editor,"ResolveSnapTarget");
if(resolve.ReturnType.Name!="SnapTarget" || resolve.GetParameters().Length!=3) throw new Exception("Unexpected signature");
var owner=new Harmony("nodelock.probe.5949");
try
{
    owner.Patch(resolve,transpiler:new HarmonyMethod(typeof(Bridge),nameof(Bridge.Transpile)));
    if(!Harmony.GetPatchInfo(resolve).Owners.Contains(owner.Id)) throw new Exception("Patch missing");
    Console.WriteLine("PASS native internal SnapTarget return bridge patches successfully");
}
finally { owner.UnpatchAll(owner.Id); }
if(Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)?.Owners.Contains(owner.Id)==true)) throw new Exception("Residual probe patch");
var nodes=new[]{new Candidate(1,2,20),new Candidate(2,2.5,10),new Candidate(3,3.1,1)};
Check(HoverPicker.Pick(nodes,1)==2,"direct tie prefers depth");
Check(HoverPicker.Pick(nodes.Reverse(),1)==2,"order independence");
Check(HoverPicker.Pick(new[]{new Candidate(1,8,2),new Candidate(2,1,10)},1)==2,"direct hit beats hysteresis");
Check(HoverPicker.Pick(new[]{new Candidate(1,8,2),new Candidate(2,7,10)},1)==1,"near tie prefers depth");
Check(HoverPicker.Pick(new[]{new Candidate(1,0,-1)},null)==null,"behind-camera rejection");
Check(HoverPicker.Pick(new[]{new Candidate(1,28,1)},null)==1,"wider acquisition includes 28px");
Check(HoverPicker.Pick(new[]{new Candidate(1,29,1)},null)==null,"acquisition excludes 29px");
Check(HoverPicker.Pick(new[]{new Candidate(1,36,1)},1)==1,"retention includes 36px");
Check(HoverPicker.Pick(new[]{new Candidate(1,37,1)},1)==null,"retention releases after 36px");
Check(HoverPicker.Pick(new[]{new Candidate(1,56,1)},null,2)==1,"acquisition scales with display DPI");
Check(HoverPicker.Pick(new[]{new Candidate(1,28,1),new Candidate(2,2,50)},1)==2,"direct hit still overrides wide retention");
var square=new[]{new ScreenPoint(0,0),new ScreenPoint(1,0),new ScreenPoint(1,1),new ScreenPoint(0,1),new ScreenPoint(.5,.5),new ScreenPoint(0,0)};
Check(Outline.Hull(square).Length==4,"outline excludes interior and duplicate mesh vertices");
Check(Outline.Hull(square).SequenceEqual(Outline.Hull(square.Reverse())),"outline independent of mesh vertex ordering");
Check(Outline.Hull(new[]{new ScreenPoint(0,0),new ScreenPoint(1,0),new ScreenPoint(2,0)}).Length==2,"edge-on outline degenerates safely");
var state=new LockState();
Check(state.Tab(true,false,false,true,true,1)&&state.Target==1,"Tab locks");
state.Tab(true,false,true,true,true,2); Check(state.Target==1,"repeat ignored");
state.Tab(false,true,false,false,false,null); state.Presented(); state.MouseDown();
state.Tab(true,false,false,true,true,2); Check(state.Mode==Mode.Idle&&!state.MouseUp(),"unlock invalidates gesture without retarget");
state.Tab(false,true,false,true,true,null);state.Tab(true,false,false,true,true,1);state.FocusLost(true);
Check(state.Mode==Mode.Recovery,"focus enters recovery");
state.Tab(true,false,false,true,true,2);Check(state.Mode==Mode.Idle&&state.Target==null,"recovery acknowledges only");
Console.WriteLine("PASS cleanup and picker/state checks. No game launched; live acceptance pending.");
Check(SourcePicker.Pick(new[]{new SourceCandidate(0,1),new SourceCandidate(1,0)},0)==0,"initial source face");
Check(SourcePicker.Pick(new[]{new SourceCandidate(0,0),new SourceCandidate(1,1)},0)==1,"quarter turn swaps face");
Check(SourcePicker.Pick(new[]{new SourceCandidate(0,-1),new SourceCandidate(2,1)},0)==2,"half turn swaps opposite face");
Check(SourcePicker.Pick(new[]{new SourceCandidate(0,.68),new SourceCandidate(1,.73)},0)==0,"fine rotation source hysteresis");
Check(SourcePicker.Pick(new[]{new SourceCandidate(0,.55),new SourceCandidate(1,.8)},0)==1,"clear side change beats hysteresis");
Check(SourcePicker.Pick(new[]{new SourceCandidate(0,-1),new SourceCandidate(1,double.NaN)},0)==null,"missing side unavailable");
Check(SourcePicker.Pick(new[]{new SourceCandidate(2,1),new SourceCandidate(1,1)},-1)==1,"stable source tie");
if(args.Length>1)
{
    var runtime=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
    var type=runtime.GetType("NodeLock.Ksa.NodeLockMod",true)!;
    var mod=Activator.CreateInstance(type)!;
    try
    {
        type.GetMethod("Load")!.Invoke(mod,null);
        var patches=Harmony.GetAllPatchedMethods().Count(m=>Harmony.GetPatchInfo(m)?.Owners.Contains("nodelock.hover.5949")==true);
        Check(patches==14,"all fourteen locking/runtime patches installed");
        type.GetMethod("ProbeNativeSeat")!.Invoke(null,null);
        Console.WriteLine("PASS actual native SeatStack and SnapTarget construction");
        type.GetMethod("ProbeRuntimeGuards")!.Invoke(null,null);
        Console.WriteLine("PASS runtime recovery/stale-preview/implicit-commit guards and claimed Tab UI suppression");
    }
    finally { type.GetMethod("Unload")!.Invoke(mod,null); }
    Check(!Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m)?.Owners.Contains("nodelock.hover.5949")==true),"hover runtime cleanup");
}
static void Check(bool pass,string label){if(!pass)throw new Exception(label);Console.WriteLine("PASS "+label);}
public static class Bridge
{
    public static object Identity(object target)=>target;
    public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        var type=((MethodInfo)__originalMethod).ReturnType;
        foreach(var i in instructions)
        {
            if(i.opcode==OpCodes.Ret)
            {
                var box=new CodeInstruction(OpCodes.Box,type);box.labels.AddRange(i.labels);i.labels.Clear();
                yield return box;yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(Bridge),nameof(Identity)));
                yield return new CodeInstruction(OpCodes.Unbox_Any,type);
            }
            yield return i;
        }
    }
}
