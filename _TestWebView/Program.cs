using System;
using System.IO;
using System.Linq;
using System.Reflection;

var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "RunModule", "bin", "Debug", "net9.0");
dir = Path.GetFullPath(dir);
Console.WriteLine($"Scanning: {dir}");

// Load Avalonia so typeof(AppBuilder) is resolvable from the correct assembly
var avaloniaAsm = Assembly.LoadFrom(Path.Combine(dir, "Avalonia.dll"));
var appBuilderType = avaloniaAsm.GetType("Avalonia.AppBuilder")!;
Console.WriteLine($"AppBuilder from Avalonia.dll: {avaloniaAsm.GetName().Version}");

var desktopDll = Path.Combine(dir, "Avalonia.WebView.Desktop.dll");
var desktopAsm = Assembly.LoadFrom(desktopDll);
var extType = desktopAsm.GetType("Avalonia.WebView.Desktop.AppBuilderExtensions")!;

// List all methods on the extension class
Console.WriteLine("\nMethods on AppBuilderExtensions:");
foreach (var m in extType.GetMethods(BindingFlags.Public | BindingFlags.Static))
{
    var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.AssemblyQualifiedName?.Split(',')[0]} {p.Name}"));
    Console.WriteLine($"  {m.Name}({parms})");
}

// Try GetMethod by name only (ignoring parameter types)
var method = extType.GetMethod("UseDesktopWebView", BindingFlags.Public | BindingFlags.Static);
Console.WriteLine($"\nGetMethod (no type constraint): {(method != null ? "FOUND" : "NOT FOUND")}");
if (method != null)
{
    var paramTypes = method.GetParameters().Select(p => $"{p.ParameterType.FullName} [{p.ParameterType.Assembly.GetName().Name} {p.ParameterType.Assembly.GetName().Version}]");
    Console.WriteLine($"  Parameter types: {string.Join(", ", paramTypes)}");
    Console.WriteLine($"  AppBuilder in test: [{avaloniaAsm.GetName().Name} {avaloniaAsm.GetName().Version}]");
}

