using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Xml.Linq;
internal static class Program {
 static void Main(string[] args) {
  AssemblyLoadContext.Default.Resolving += (_,n)=> {var p=Path.Combine(args[0],n.Name+".dll");return File.Exists(p)?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(p)):null;};
  Verify(args);
 }
 [MethodImpl(MethodImplOptions.NoInlining)] static void Verify(string[] args) {
  var batteryIds=new[]{"CoreElectricalA_Prefab_RadialBatterySmallV1A","CoreElectricalA_Prefab_RadialBatteryMediumV1A"};
  var batteryRows=new List<XElement>();
  foreach(var name in new[]{"CoreElectricalAAssets.xml","CoreElectricalAGameData.xml"})
   batteryRows.AddRange(XDocument.Load(Path.Combine(args[0],"Content/Core",name)).Root.Elements("Part").Where(x=>batteryIds.Contains((string)x.Attribute("Id"))).Select(x=>new XElement(x)));
  var batteryErrors=new KSA.ContentErrors();
  var batteryBefore=KSA.ContentPrePass.Resolve(batteryRows.Select(x=>new XElement(x)).ToList(),batteryErrors);
  if(batteryErrors.Count!=0)throw new Exception("Battery baseline errors");
  batteryRows.AddRange(XDocument.Load(Path.Combine(Path.GetDirectoryName(args[2]),"BatteryBayCompat.xml")).Root.Elements().Select(x=>new XElement(x)));
  batteryErrors=new KSA.ContentErrors();
  var batteryAfter=KSA.ContentPrePass.Resolve(batteryRows,batteryErrors);
  if(batteryErrors.Count!=0)throw new Exception("Battery patch errors");
  foreach(var id in batteryIds){
   var batteryOriginal=new XElement(batteryBefore.Single(x=>x.Id==id).Row);
   var batteryActual=new XElement(batteryAfter.Single(x=>x.Id==id).Row);
   var key=id.Contains("Small")?"_connector3":"_connector4";
   var original=batteryOriginal.Element("Connectors").Elements("Connector").Single(x=>(string)x.Attribute("Key")==key).Element("Transform").Element("Scale");
   var actual=batteryActual.Element("Connectors").Elements("Connector").Single(x=>(string)x.Attribute("Key")==key).Element("Transform").Element("Scale");
   foreach(var axis in new[]{"X","Y","Z"})if((string)actual.Attribute(axis)!="0.25")throw new Exception("Wrong battery scale");
   actual.ReplaceWith(new XElement(original));
   if(!XNode.DeepEquals(batteryOriginal,batteryActual))throw new Exception("Unexpected battery metadata change");
   Console.WriteLine("PASS native battery merge: "+id);
  }
  var sphere="TesseraTankA_Prefab_SphereTank";
  var ids=new[]{sphere+"A",sphere+"B",sphere+"C",sphere+"D","CoreFuelTankA_Prefab_RadialMPQuarterWA","CoreFuelTankA_Prefab_RadialMPHalfWA"};
  var rows=new List<XElement>();
  foreach(var file in new[]{Path.Combine(args[0],"Content/Core/CoreFuelTankAAssets.xml"),Path.Combine(args[0],"Content/Core/CoreFuelTankAGameData.xml"),Path.Combine(args[1],"TesseraTankAAssets.xml"),Path.Combine(args[1],"TesseraTankAGameData.xml")})
   rows.AddRange(XDocument.Load(file).Root.Elements("Part").Where(x=>ids.Contains((string)x.Attribute("Id"))).Select(x=>new XElement(x)));
  var errors=new KSA.ContentErrors();var before=KSA.ContentPrePass.Resolve(rows.Select(x=>new XElement(x)).ToList(),errors);
  if(errors.Count!=0)throw new Exception("Baseline resolve errors");
  rows.AddRange(XDocument.Load(args[2]).Root.Elements().Select(x=>new XElement(x)));
  // An absent optional Tessera must not create placeholder parts or errors.
  var stockRows=rows.Where(x=>!((string)x.Attribute("Id")).StartsWith(sphere) || (string)x.Attribute("Modify")=="PatchIfExists").Select(x=>new XElement(x)).ToList();
  var stockErrors=new KSA.ContentErrors();
  var stockOnly=KSA.ContentPrePass.Resolve(stockRows,stockErrors);
  if(stockErrors.Count!=0 || stockOnly.Any(x=>x.Id.StartsWith(sphere)))throw new Exception("Absent optional Tessera is not a clean no-op");
  Console.WriteLine("PASS bundled patch without Tessera: no errors or phantom parts");
  errors=new KSA.ContentErrors();var after=KSA.ContentPrePass.Resolve(rows,errors);
  if(errors.Count!=0)throw new Exception("Patched resolve errors");
  foreach(var id in ids){
   var original=new XElement(before.Single(x=>x.Id==id).Row);var actual=new XElement(after.Single(x=>x.Id==id).Row);
   var ac=actual.Element("Connectors");var oc=original.Element("Connectors");
   if(id.StartsWith(sphere)){
    foreach(var key in new[]{"_connector01Foot","_connector02Equator"}){
     var scale=ac.Elements("Connector").Single(x=>(string)x.Attribute("Key")==key).Element("Transform").Element("Scale");
     foreach(var axis in new[]{"X","Y","Z"}){if((string)scale.Attribute(axis)!="0.25")throw new Exception("Wrong scale");scale.SetAttributeValue(axis,"0.5");}
    }
   }else if(id.EndsWith("HalfWA")){
    var mount=ac.Elements("Connector").Single();
    if((string)mount.Attribute("Key")!="SurfaceMount" || mount.Element("Flags")!=null || (string)mount.Element("Transform").Element("Position").Attribute("Z")!="0.325")throw new Exception("Expected existing base converted to ordinary node");
    foreach(var axis in new[]{"X","Y","Z"})if((string)mount.Element("Transform").Element("Scale").Attribute(axis)!="0.25")throw new Exception("Wrong base size");
    ac.ReplaceWith(new XElement(oc));
   }else{
    var added=ac.Elements("Connector").Where(x=>((string)x.Attribute("Key")).StartsWith("NodeLockBay")).ToArray();
    if(added.Length!=1 || (string)added[0].Attribute("Key")!="NodeLockBayPosZ")throw new Exception("Expected only the base node");
    var transform=added[0].Element("Transform");
    var expectedZ=id.EndsWith("QuarterWA") ? "0.15" : "0.325";
    if((string)transform.Element("Position").Attribute("Z")!=expectedZ || (string)transform.Element("Rotation").Attribute("Y")!="-1.570796326794897")throw new Exception("Wrong base transform");
    foreach(var c in added){if(c.Element("Flags")!=null || (string)c.Element("Capabilities")!="BulkFluid")throw new Exception("Wrong node type");foreach(var axis in new[]{"X","Y","Z"})if((string)c.Element("Transform").Element("Scale").Attribute(axis)!="0.25")throw new Exception("Wrong node size");c.Remove();}
    if(oc==null)ac.Remove();
   }
   if(!XNode.DeepEquals(original,actual))throw new Exception("Unintended metadata change: "+id);
   Console.WriteLine("PASS native merge: "+id);
  }
 }
}
