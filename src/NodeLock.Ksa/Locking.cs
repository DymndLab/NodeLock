using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using NodeLock.Core;
using RenderCore.Input;

namespace NodeLock.Ksa;

public sealed partial class NodeLockMod
{
    private static Part? lockedRoot;
    private static Part.Connector? source, destination;
    private static bool recovery, ownedTab, suppressTab, previewBuilt, presented, mouseArmed, authorizedCommit, guarded;
    private static string notice
    {
        set { if(!string.IsNullOrEmpty(value))FlashError(value); }
    }
    private static object? lastBuilt;
    private static doubleQuat userRotationAtLock, orientationAtLock;
    private static bool sideUnavailable;
    private static string placementProblem="";
    private static readonly Type GhostType=typeof(VehicleEditor).GetNestedType("GhostTransform",BindingFlags.NonPublic)!;
    private static readonly MethodInfo ComputeGhosts=AccessTools.Method(typeof(VehicleEditor),"ComputeGhostTransforms");
    private static readonly MethodInfo GhostMatrix=AccessTools.Method(GhostType,"ToMatrix");
    private static readonly IList copyGhosts=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(GhostType))!;
    private static readonly IList localGhosts=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(GhostType))!;
    private static readonly List<Part> mirrorParents=new();
    private static int copyCount;

    private static readonly InputAction[] RotationActions = [InputAction.EditorPartPitchDown,InputAction.EditorPartPitchUp,InputAction.EditorYawPartLeft,InputAction.EditorYawPartRight,InputAction.EditorRollPartLeft,InputAction.EditorRollPartRight];
    private static readonly Type TargetType=typeof(VehicleEditor).GetNestedType("SnapTarget",BindingFlags.NonPublic)!;
    private static readonly Type MathType=typeof(VehicleEditor).Assembly.GetType("KSA.EditorSnapMath",true)!;
    private static readonly Type InputType=MathType.GetNestedType("StackSeatInput",BindingFlags.NonPublic)!;
    private static readonly MethodInfo Seat=AccessTools.Method(MathType,"SeatStack");
    private static readonly ConstructorInfo PoseCtor=Seat.ReturnType.GetConstructors().Single(c=>c.GetParameters().Length==2);
    private static readonly PropertyInfo PoseRotation=Seat.ReturnType.GetProperty("Rotation")!;
    private static readonly ConstructorInfo TargetCtor=TargetType.GetConstructors().Single(c=>c.GetParameters().Length==9);
    private static readonly ConstructorInfo InputCtor=InputType.GetConstructors().Single(c=>c.GetParameters().Length==8);
    private static readonly object None=AccessTools.Field(TargetType,"None").GetValue(null)!;
    private static readonly object StackKind=Enum.Parse(TargetCtor.GetParameters()[0].ParameterType,"Stack");
    private static readonly FieldInfo Preview=AccessTools.Field(typeof(VehicleEditor),"_previewTarget");
    private static readonly MethodInfo ClearPreview=AccessTools.Method(typeof(VehicleEditor),"ClearGrabPreview");
    private static readonly Type Backend=typeof(VehicleEditor).Assembly.GetType("KSA.ImGuiBackendGlfwImpl",true)!;

    private static void ValidateBindings()
    {
        if(AccessTools.Method(typeof(VehicleEditor),"ResolveSnapTarget").ReturnType!=TargetType || Preview.FieldType!=TargetType || Seat.GetParameters()[0].ParameterType!=InputType.MakeByRefType())throw new MissingMemberException("Native seat/target signature changed");
    }
    private static void Dirty(){previewBuilt=false;presented=false;mouseArmed=false;}
    private static void ResetLock(){faultCondition="";flashText="";flashUntil=0;copyGhosts.Clear();localGhosts.Clear();mirrorParents.Clear();copyCount=0;placementProblem="";sideUnavailable=false;lockedRoot=null;source=null;destination=null;recovery=false;guarded=false;ownedTab=false;suppressTab=false;authorizedCommit=false;notice="";lastBuilt=null;Dirty();}
    private static void InvalidateLock()
    {
        lockedRoot=null;source=null;destination=null;recovery=guarded=true;notice="";Dirty();
        if(editor!=null)ClearPreview.Invoke(editor,null);
    }
    private static bool UiBlocked(VehicleEditor e)=>e.IsUserTyping||ImGui.GetIO().WantTextInput||KSA.Program.ConsoleWindow.IsOpen||GameSettings.Show||Popup.AnyOpen||ImGui.GetIO().WantCaptureKeyboard;
    private static string? UnsupportedReason(VehicleEditor e,Part root)
    {
        if(root.Tree==null||root.Tree.IsDisposed)return "NodeLock: carried part is not ready; pick it up again.";
        var iterator=new PartTreeChildrenIterator(root); Part? p;
        while((p=iterator.GetNextNode())!=null)
            if(!ReferenceEquals(p,root))return "NodeLock: carried part has attached children; pick up one part without children.";
        return null;
    }
    private static bool Supported(VehicleEditor e,Part root)=>UnsupportedReason(e,root)==null;
    private static bool Valid(VehicleEditor e)
    {
        if(lockedRoot==null||source==null||destination==null||!ReferenceEquals(e.Highlighted,lockedRoot)||!lockedRoot.Grabbed||!Supported(e,lockedRoot))return false;
        if(destination.Parent.Tree==null||destination.Parent.Tree.IsDisposed||!source.Parent.Connectors.Contains(source)||!destination.Parent.Connectors.Contains(destination))return false;
        bool found=false;foreach(var p in e.EditingSpace.AllParts)if(ReferenceEquals(p,destination.Parent)){found=true;break;}
        return found&&!ReferenceEquals(source.Parent,destination.Parent)&&Ordinary(source)&&Ordinary(destination)&&source.CanConnect(destination);
    }
    private static object MakeTarget(VehicleEditor e)
    {
        var root=lockedRoot!;var from=source!;var to=destination!;
        var rotation=(root.GrabbedUserRotation*doubleQuat.Inverse(userRotationAtLock)*orientationAtLock).NormalizeOrIdentity();
        var facing=-double3.UnitX.Transform(to.Asmb2VehicleAsmb);
        var choices=root.Connectors.Select((c,i)=>(c,i))
            .Where(x=>Ordinary(x.c)&&x.c.CanConnect(to))
            .Select(x=>new SourceCandidate(x.i,double3.Dot(double3.UnitX.Transform(x.c.Asmb2ParentAsmb).Transform(rotation),facing)));
        var selected=SourcePicker.Pick(choices,Array.IndexOf(root.Connectors,from));
        placementProblem="No compatible connector on this side. Preview stays anchored; rotate back or Tab to unlock.";
        sideUnavailable=!selected.HasValue;
        // An unavailable side is still a locked preview. Retain the last real
        // connector as the visual pivot; block commit, never return free placement.
        if(selected.HasValue)source=from=root.Connectors[selected.Value];
        else Dirty();
        var pose=AnchorPose(orientationAtLock,userRotationAtLock,root.GrabbedUserRotation,from.PositionParentAsmb,to.PositionVehicleAsmb);
        var target=BuildTarget(root,to.Parent,Array.IndexOf(to.Parent.Connectors,to),Array.IndexOf(root.Connectors,from),e.SymmetryAmount,e.InheritSymmetry,pose);
        if(!CopiesAvailable(target,from,to)){sideUnavailable=true;Dirty();}
        return target;
    }
    private static object BuildTarget(Part? root,Part? parent,int targetIndex,int sourceIndex,int symmetry,bool inherit,object pose)
        =>TargetCtor.Invoke(new object?[]{StackKind,root,parent,parent,targetIndex,sourceIndex,symmetry,inherit,pose});

    // Ask the same native routine used by PreviewSnapTarget for the copies.
    // Check their intended connector positions before allowing the native commit.
    private static bool CopiesAvailable(object target,Part.Connector from,Part.Connector to)
    {
        mirrorParents.Clear();
        ComputeGhosts.Invoke(null,new object[]{target,copyGhosts,localGhosts,mirrorParents});
        copyCount=copyGhosts.Count;
        var parents=mirrorParents.Prepend(to.Parent).Distinct().ToArray();
        var claimed=new HashSet<Part.Connector>{to};
        foreach(var ghost in copyGhosts)
        {
            var matrix=(double4x4)GhostMatrix.Invoke(ghost,null)!;
            var point=to.PositionVehicleAsmb.Transform(matrix);
            Part.Connector? match=null;
            foreach(var parent in parents)
            {
                if(parent.Tree==null||parent.Tree.IsDisposed)continue;
                foreach(var node in parent.Connectors)
                    if(!claimed.Contains(node)&&Ordinary(node)&&from.CanConnect(node)&&(node.PositionVehicleAsmb-point).LengthSquared()<=1e-6)
                    {match=node;break;}
                if(match!=null)break;
            }
            if(match==null){placementProblem="A symmetry copy has no free compatible node. Choose another destination or change symmetry.";return false;}
            claimed.Add(match);
        }
        return true;
    }
    private static bool SettingsMatch(VehicleEditor e,object? target)=>target!=null
        && (int)TargetType.GetProperty("SymmetryAmount")!.GetValue(target)! ==e.SymmetryAmount
        && (bool)TargetType.GetProperty("Inherit")!.GetValue(target)! ==e.InheritSymmetry;
    private static doubleQuat? NextUsableRotation(doubleQuat before,doubleQuat requested,int steps,Func<doubleQuat,bool> usable)
    {
        var delta=(doubleQuat.Inverse(before)*requested).NormalizeOrIdentity();
        var trial=requested;
        for(int step=2;step<steps;step++)
        {
            trial=(trial*delta).NormalizeOrIdentity();
            if(usable(trial))return trial;
        }
        return null;
    }
    private static object AnchorPose(doubleQuat baseline,doubleQuat initial,doubleQuat current,double3 sourcePoint,double3 destinationPoint)
    {
        var rotation=(current*doubleQuat.Inverse(initial)*baseline).NormalizeOrIdentity();
        var position=destinationPoint-sourcePoint.Transform(rotation);
        return PoseCtor.Invoke(new object[]{position,rotation});
    }
    public static object ReplaceTarget(object native,VehicleEditor current)
    {
        if(!enabled)return native;
        try
        {
            if(lockedRoot!=null)
            {
                if(!Valid(current)||UiBlocked(current)){InvalidateLock();return None;}
                var target=MakeTarget(current);
                if(lastBuilt==null||!lastBuilt.Equals(target)){Dirty();lastBuilt=target;}
                previewBuilt=!sideUnavailable;return target;
            }
            if(recovery)return None;
            if(guarded){if(lastBuilt==null||!lastBuilt.Equals(native)){Dirty();lastBuilt=native;}previewBuilt=true;}
            return native;
        }
        catch(Exception error){Console.WriteLine("[NodeLock] Seating rejected: "+error);InvalidateLock();return None;}
    }

    private static bool HandleTab(GlfwWindow window,GlfwKey key,GlfwKeyAction action,GlfwModifier mods)
    {
        if(key!=GlfwKey.Tab)return false;
        if(ownedTab){if(action==GlfwKeyAction.Release)ownedTab=false;return true;}
        if(!enabled||action!=GlfwKeyAction.Press||((int)mods&15)!=0)return false;
        var e=KSA.Program.Editor;
        if(e?.Highlighted is not {Grabbed:true} root||UiBlocked(e)||ImGui.GetIO().WantCaptureMouse||(bool)Looking.GetValue(null)!)return false;
        var check=new GlfwKeyEvent(window,action,key,mods);
        foreach(var binding in Enum.GetValues<InputAction>())
            if(binding.ToString().StartsWith("Editor",StringComparison.Ordinal)&&Input.Matches(in check,binding)){notice="Tab is assigned to an editor action; NodeLock yields to that binding.";return false;}
        var viewport=KSA.Program.InputViewport;var cursor=ImGui.GetMousePos();
        if(cursor.X<viewport.Position.X||cursor.Y<viewport.Position.Y||cursor.X>=viewport.Position.X+viewport.Size.X||cursor.Y>=viewport.Position.Y+viewport.Size.Y)return false;
        ownedTab=true;notice="";
        if(lockedRoot!=null||recovery){lockedRoot=null;source=null;destination=null;recovery=false;sideUnavailable=false;guarded=true;Dirty();ClearPreview.Invoke(e,null);return true;}
        Update(e,viewport);
        if(UnsupportedReason(e,root) is {} reason){notice=reason;Console.WriteLine("[NodeLock] "+reason);return true;}
        if(hover==null){notice="No compatible node under cursor.";return true;}
        var target=hover;
        // Prefer the current native pair; otherwise the least connector-axis alignment.
        var choices=root.Connectors.Where(c=>Ordinary(c)&&c.CanConnect(target)).ToArray();
        if(choices.Length==0)return true;
        var native=Preview.GetValue(e)!;
        var parent=TargetType.GetProperty("ParentPart")!.GetValue(native);
        int parentIndex=(int)TargetType.GetProperty("ParentConnectorIndex")!.GetValue(native)!;
        int sourceIndex=(int)TargetType.GetProperty("GrabbedConnectorIndex")!.GetValue(native)!;
        source=ReferenceEquals(parent,target.Parent)&&parentIndex==Array.IndexOf(target.Parent.Connectors,target)&&sourceIndex>=0&&sourceIndex<root.Connectors.Length&&choices.Contains(root.Connectors[sourceIndex]) ? root.Connectors[sourceIndex] : choices.OrderBy(c=>double3.Dot(double3.UnitX.Transform(c.Asmb2VehicleAsmb),double3.UnitX.Transform(target.Asmb2VehicleAsmb))).First();
        destination=target;lockedRoot=root;userRotationAtLock=root.GrabbedUserRotation;
        var input=InputCtor.Invoke(new object[]{root.PositionParentAsmb,root.Asmb2ParentAsmb,root.Asmb2ParentAsmbSafe,userRotationAtLock,source.PositionParentAsmb,source.Asmb2ParentAsmb,target.PositionVehicleAsmb,target.Asmb2VehicleAsmb});
        orientationAtLock=(doubleQuat)PoseRotation.GetValue(Seat.Invoke(null,new[]{input})!)!;
        sideUnavailable=false;guarded=true;recovery=false;lastBuilt=null;Dirty();return true;
    }

    // Keep native OwnedKeys and modifier bookkeeping. Only replace UI event delivery
    // for the key cycle claimed by this mod; Program forwarding is suppressed separately.
    public static void QueueKey(ImGuiIOPtr io,ImGuiKey key,bool down){if(!suppressTab)io.AddKeyEvent(key,down);}
    [HarmonyPatch]
    private static class BackendKey
    {
        static MethodBase TargetMethod()=>AccessTools.Method(Backend,"OnKey");
        static void Prefix(GlfwWindow window,GlfwKey key,GlfwKeyAction action,GlfwModifier mods)
        {
            suppressTab=false;
            try{suppressTab=HandleTab(window,key,action,mods);}catch(Exception e){Console.WriteLine("[NodeLock] Input rejected: "+e);InvalidateLock();suppressTab=key==GlfwKey.Tab;}
        }
        static void Postfix()=>suppressTab=false;
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced=0;
            foreach(var i in instructions)
            {
                if(i.operand is MethodInfo method&&method.Name=="AddKeyEvent"&&method.IsStatic&&method.GetParameters().Select(p=>p.ParameterType).SequenceEqual(new[]{typeof(ImGuiIOPtr),typeof(ImGuiKey),typeof(bool)})){i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(NodeLockMod),nameof(QueueKey));replaced++;}
                yield return i;
            }
            if(replaced!=1)throw new InvalidOperationException("Unexpected backend key queue shape");
        }
    }
    [HarmonyPatch(typeof(KSA.Program),nameof(KSA.Program.OnKey))]
    private static class ForwardKey{static bool Prefix(GlfwKey key)=>!(key==GlfwKey.Tab&&suppressTab);}
    [HarmonyPatch(typeof(VehicleEditor),"ResolveSnapTarget")]
    private static class Resolve
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach(var i in instructions)
            {
                if(i.opcode==OpCodes.Ret){var box=new CodeInstruction(OpCodes.Box,TargetType);box.labels.AddRange(i.labels);i.labels.Clear();yield return box;yield return new CodeInstruction(OpCodes.Ldarg_0);yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(NodeLockMod),nameof(ReplaceTarget)));yield return new CodeInstruction(OpCodes.Unbox_Any,TargetType);}
                yield return i;
            }
        }
    }
    [HarmonyPatch(typeof(VehicleEditor),nameof(VehicleEditor.OnKey))]
    private static class EditorKey
    {
        static bool Prefix(GlfwKeyEvent keyEvent,ref bool __result,out (Part? Root,doubleQuat Before,int Steps) __state)
        {
            __state=default;
            if(lockedRoot==null&&!recovery)return true;
            var (window,action,key,mods)=keyEvent;
            if(action!=GlfwKeyAction.Press)return true;
            if(key==GlfwKey.Escape){InvalidateLock();return true;}
            var fineEvent=new GlfwKeyEvent(window,action,key,mods&~GlfwModifier.Shift);
            if(lockedRoot!=null && RotationActions.Any(a=>Input.MatchPressed(in fineEvent,a)))
                __state=(lockedRoot,lockedRoot.GrabbedUserRotation,(mods&GlfwModifier.Shift)!=0 ? 72 : 4);
            if(RotationActions.Any(a=>Input.MatchPressed(in fineEvent,a)) || Input.MatchPressed(in keyEvent,InputAction.EditorNextSymmetryMode) || Input.MatchPressed(in keyEvent,InputAction.EditorPrevSymmetryMode) || Input.MatchPressed(in keyEvent,InputAction.EditorToggleSymmetryInheritance)){notice="";Dirty();return true;}
            // Other tool/history/part changes still require an explicit unlock.
            if(Enum.GetValues<InputAction>().Any(a=>a.ToString().StartsWith("Editor",StringComparison.Ordinal)&&a!=InputAction.EditorCameraFocus&&Input.Matches(in fineEvent,a)) || ((int)mods&2)!=0)
            {notice="Tab to unlock before changing parts or tools.";__result=true;return false;}
            return true;
        }
        static void Postfix((Part? Root,doubleQuat Before,int Steps) __state)
        {
            if(__state.Root==null||!ReferenceEquals(__state.Root,lockedRoot)||destination==null||source==null)return;
            try
            {
                var root=__state.Root;var targetNode=destination;var requested=root.GrabbedUserRotation;
                var facing=-double3.UnitX.Transform(destination.Asmb2VehicleAsmb);
                bool Usable(doubleQuat user,bool exact)
                {
                    var rotation=(user*doubleQuat.Inverse(userRotationAtLock)*orientationAtLock).NormalizeOrIdentity();
                    return root.Connectors.Any(c=>Ordinary(c)&&c.CanConnect(targetNode)&&double3.Dot(double3.UnitX.Transform(c.Asmb2ParentAsmb).Transform(rotation),facing)>=(exact ? .9999 : SourcePicker.MinimumAlignment));
                }
                if(Usable(requested,false))return;
                var next=NextUsableRotation(__state.Before,requested,__state.Steps,q=>Usable(q,true));
                root.GrabbedUserRotation=next ?? __state.Before;
                if(!next.HasValue)notice="No other usable side in that direction; kept previous orientation.";
                Dirty();
            }
            catch(Exception error){Console.WriteLine("[NodeLock] Directional rotation rejected: "+error);InvalidateLock();}
        }
    }

    [HarmonyPatch(typeof(VehicleEditor),nameof(VehicleEditor.OnMouseButton))]
    private static class Mouse
    {
        static bool Prefix(VehicleEditor __instance,GlfwMouseButton button,GlfwButtonAction action,ref bool __result)
        {
            authorizedCommit=false;
            if(!guarded||button!=GlfwMouseButton.Number1)return true;
            if(action==GlfwButtonAction.Press){mouseArmed=presented&&!recovery&&!UiBlocked(__instance);return true;}
            if(action!=GlfwButtonAction.Release)return true;
            if(sideUnavailable||recovery||!mouseArmed||!presented||UiBlocked(__instance)||(lockedRoot!=null&&(!Valid(__instance)||!SettingsMatch(__instance,lastBuilt)))){mouseArmed=false;__result=true;return false;}
            mouseArmed=false;authorizedCommit=true;return true;
        }
        static void Postfix(VehicleEditor __instance){authorizedCommit=false;if(guarded&&__instance.Highlighted is not {Grabbed:true})ResetLock();}
    }
    [HarmonyPatch(typeof(VehicleEditor),"CommitGrabPreview")]
    private static class Commit{static bool Prefix()=>!guarded||(!sideUnavailable&&(lockedRoot==null||(editor!=null&&Valid(editor)&&SettingsMatch(editor,lastBuilt)&&CopiesAvailable(lastBuilt!,source!,destination!)))&&(authorizedCommit||(lockedRoot==null&&!recovery&&presented)));}
    [HarmonyPatch]
    private static class ImplicitDrop
    {
        static IEnumerable<MethodBase> TargetMethods()=>new[]{AccessTools.Method(typeof(VehicleEditor),"DropGrabbedPart"),AccessTools.Method(typeof(VehicleEditor),"ClearHighlightedState")};
        static bool Prefix(){if(!guarded||(lockedRoot==null&&!recovery&&presented))return true;notice="Tab to unlock before changing tools or parts.";return false;}
    }
    [HarmonyPatch(typeof(VehicleEditor),nameof(VehicleEditor.OnScroll))]
    private static class Scroll{static bool Prefix(VehicleEditor __instance,ref bool __result){if(lockedRoot==null||!__instance.ControlHeld)return true;__result=true;return false;}}
    [HarmonyPatch(typeof(KSA.Program),nameof(KSA.Program.OnFocus))]
    private static class Focus{static void Prefix(GlfwFocusEvent focusEvent){if(focusEvent==GlfwFocusEvent.LostFocus){ownedTab=false;if(lockedRoot!=null)InvalidateLock();}}}

    // Headless exercise of the actual private native constructor and SeatStack call.
    public static void ProbeNativeSeat()
    {
        ValidateBindings();
        var input=InputCtor.Invoke(new object[]{double3.Zero,doubleQuat.Identity,doubleQuat.Identity,doubleQuat.Identity,double3.Zero,doubleQuat.Identity,new double3(2,3,4),doubleQuat.Identity});
        var pose=Seat.Invoke(null,new[]{input})!;
        var position=(double3)pose.GetType().GetProperty("Position")!.GetValue(pose)!;
        if((position-new double3(2,3,4)).LengthSquared()>1e-12)throw new Exception("Native seat position mismatch");
        var target=TargetCtor.Invoke(new object?[]{StackKind,null,null,null,0,0,0,false,pose});
        if(!Equals(TargetType.GetProperty("Seat")!.GetValue(target),pose))throw new Exception("Target seat mismatch");
        foreach(var amount in new[]{0,1,2,4,8})
        foreach(var inherit in new[]{false,true})
        {
            var configured=BuildTarget(null,null,3,5,amount,inherit,pose);
            if((int)TargetType.GetProperty("SymmetryAmount")!.GetValue(configured)! != amount || (bool)TargetType.GetProperty("Inherit")!.GetValue(configured)! != inherit
                || (int)TargetType.GetProperty("GrabbedConnectorIndex")!.GetValue(configured)! != 5)
                throw new Exception("Symmetry/source target settings lost");
        }
        var settingsEditor=(VehicleEditor)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(VehicleEditor));
        AccessTools.Field(typeof(VehicleEditor),"Symmetries").SetValue(settingsEditor,new[]{1,2,3,4,6,8});
        settingsEditor.InheritSymmetry=true;
        var settingsTarget=BuildTarget(null,null,0,0,settingsEditor.SymmetryAmount,true,pose);
        if(!SettingsMatch(settingsEditor,settingsTarget))throw new Exception("Current settings rejected");
        settingsEditor.InheritSymmetry=false;
        if(SettingsMatch(settingsEditor,settingsTarget))throw new Exception("Stale inheritance accepted");
        var staleCount=BuildTarget(null,null,0,0,settingsEditor.SymmetryAmount+1,false,pose);
        if(SettingsMatch(settingsEditor,staleCount))throw new Exception("Stale symmetry count accepted");
        var about=AccessTools.Method(GhostType,"AboutAxis");
        var after=AccessTools.Method(GhostType,"After");
        var center=new double3(2,3,4);var mount=new double3(2,4,4);
        var local=about.Invoke(null,new object[]{double3.UnitX,Math.PI/2,center})!;
        var inherited=about.Invoke(null,new object[]{double3.UnitZ,Math.PI,new double3(1,0,0)})!;
        var combined=after.Invoke(inherited,new[]{local})!;
        var lm=(double4x4)GhostMatrix.Invoke(local,null)!;var im=(double4x4)GhostMatrix.Invoke(inherited,null)!;
        var cm=(double4x4)GhostMatrix.Invoke(combined,null)!;
        if((mount.Transform(cm)-mount.Transform(lm).Transform(im)).LengthSquared()>1e-18)throw new Exception("Inherited local ghost composition mismatch");
        Console.WriteLine("PASS symmetry/inheritance target propagation and native combined ghost transform");
        // Regression: a single compatible top port turned sideways must remain
        // visually seated even though the source picker rejects that side.
        var sideways=doubleQuat.CreateFromAxisAngle(double3.UnitY,Math.PI/2);
        if(SourcePicker.Pick(new[]{new SourceCandidate(0,double3.Dot(double3.UnitX.Transform(sideways),double3.UnitX))},0)!=null)
            throw new Exception("Sideways single connector unexpectedly eligible");
        var retainedOffset=new double3(.675,0,0);var destinationPoint=new double3(2,3,4);
        var blockedPose=AnchorPose(doubleQuat.Identity,doubleQuat.Identity,sideways,retainedOffset,destinationPoint);
        var blockedTarget=BuildTarget(null,null,0,0,1,true,blockedPose);
        var blockedPosition=(double3)blockedPose.GetType().GetProperty("Position")!.GetValue(blockedPose)!;
        if(blockedTarget.Equals(None)||(blockedPosition+retainedOffset.Transform(sideways)-destinationPoint).LengthSquared()>1e-18)
            throw new Exception("Unavailable-side preview lost its anchor");
        foreach(var direction in new[]{-1,1})
        {
            var quarter=doubleQuat.CreateFromAxisAngle(double3.UnitZ,direction*Math.PI/2);
            var half=NextUsableRotation(doubleQuat.Identity,quarter,4,q=>double3.Dot(double3.UnitX.Transform(q),-double3.UnitX)>.9999);
            if(!half.HasValue||double3.Dot(double3.UnitX.Transform(half.Value),-double3.UnitX)<.9999)throw new Exception("Did not skip missing quarter-turn side");
            var third=NextUsableRotation(doubleQuat.Identity,quarter,4,q=>double3.Dot(double3.UnitX.Transform(q),double3.UnitY*(-direction))>.9999);
            if(!third.HasValue)throw new Exception("Lost directed three-quarter turn");
            if(NextUsableRotation(doubleQuat.Identity,quarter,4,q=>double3.Dot(double3.UnitX.Transform(q),double3.UnitX)>.9999)!=null)throw new Exception("Full turn accepted as alternate side");
        }
        Console.WriteLine("PASS directional skip forward/reverse, half/three-quarter turn, no alternate");
        // Six-sided fixture: select using real quaternion transforms, then verify
        // the selected (asymmetric) source offset lands on the destination.
        var normals=new[]{double3.UnitX,-double3.UnitX,double3.UnitY,-double3.UnitY,double3.UnitZ,-double3.UnitZ};
        foreach(var axis in new[]{double3.UnitX,double3.UnitY,double3.UnitZ})
        foreach(var angle in new[]{0.0,Math.PI/2,Math.PI,3*Math.PI/2})
        {
            var r=doubleQuat.CreateFromAxisAngle(axis,angle);
            var chosen=SourcePicker.Pick(normals.Select((n,i)=>new SourceCandidate(i,double3.Dot(n.Transform(r),double3.UnitX))),0);
            if(chosen==null || double3.Dot(normals[chosen.Value].Transform(r),double3.UnitX)<.999999)throw new Exception("Wrong D6 side");
            var offsetPoint=normals[chosen.Value]*(chosen.Value+1)*.25;
            var seat=AnchorPose(doubleQuat.Identity,doubleQuat.Identity,r,offsetPoint,new double3(2,3,4));
            var seatPosition=(double3)seat.GetType().GetProperty("Position")!.GetValue(seat)!;
            if((seatPosition+offsetPoint.Transform(r)-new double3(2,3,4)).LengthSquared()>1e-18)throw new Exception("Switched source missed destination");
        }
        var initial=doubleQuat.CreateFromAxisAngle(double3.UnitY,0.7);
        var baseline=doubleQuat.CreateFromAxisAngle(double3.UnitZ,0.3);
        var offset=new double3(0.25,-0.5,0.75);
        var anchor=new double3(2,3,4);
        foreach(var axis in new[]{double3.UnitX,double3.UnitY,double3.UnitZ})
        foreach(var angle in new[]{0.0,Math.PI/36,Math.PI/2,Math.PI})
        {
            var delta=doubleQuat.CreateFromAxisAngle(axis,angle);
            var anchored=AnchorPose(baseline,initial,delta*initial,offset,anchor);
            var p=(double3)anchored.GetType().GetProperty("Position")!.GetValue(anchored)!;
            var r=(doubleQuat)PoseRotation.GetValue(anchored)!;
            if((p+offset.Transform(r)-anchor).LengthSquared()>1e-18)throw new Exception("Rotation moved attachment point");
            foreach(var basis in new[]{double3.UnitX,double3.UnitY,double3.UnitZ})
                if((basis.Transform(r)-basis.Transform(delta*baseline)).LengthSquared()>1e-18)throw new Exception("Pitch/yaw/roll was lost");
        }
    }

    public static void ProbeRuntimeGuards()
    {
        static bool InvokeBool(Type t,string method,params object?[] args)=>(bool)t.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,args)!;
        try
        {
            guarded=true;recovery=true;presented=true;authorizedCommit=false;
            if(InvokeBool(typeof(Commit),"Prefix")||InvokeBool(typeof(ImplicitDrop),"Prefix"))throw new Exception("Recovery allowed implicit commit");
            sideUnavailable=true;authorizedCommit=true;
            if(InvokeBool(typeof(Commit),"Prefix"))throw new Exception("Unavailable side allowed commit");
            sideUnavailable=false;authorizedCommit=false;
            recovery=false;presented=false;
            if(InvokeBool(typeof(Commit),"Prefix")||InvokeBool(typeof(ImplicitDrop),"Prefix"))throw new Exception("Stale preview allowed commit");
            presented=true;
            if(!InvokeBool(typeof(Commit),"Prefix")||!InvokeBool(typeof(ImplicitDrop),"Prefix"))throw new Exception("Explicit unlock blocked after fresh preview");
            mouseArmed=true;Dirty();if(mouseArmed||presented||previewBuilt)throw new Exception("Dirty did not revoke gesture");
            suppressTab=true;QueueKey(default,ImGuiKey.Tab,true);QueueKey(default,ImGuiKey.Tab,false);
            // A claimed Tab event never dereferences the null UI pointer in this fixture.
        }
        finally{ResetLock();}
    }
}
