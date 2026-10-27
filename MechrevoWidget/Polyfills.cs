// .NET Framework 4.8 下补齐新版 C# / BCL 的少量缺口,让代码保持现代写法。

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    /// <summary>record / init 访问器需要这个类型(.NET 5+ 内置)。</summary>
    internal static class IsExternalInit { }
}

namespace MechrevoWidget
{
    internal static class Compat
    {
        public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        /// <summary>当前 exe 的完整路径(相当于 .NET 6+ 的 Environment.ProcessPath)。</summary>
        public static string? ProcessPath
        {
            get
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetCurrentProcess();
                    return p.MainModule?.FileName;
                }
                catch { return null; }
            }
        }

        public static int ProcessId
        {
            get
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                return p.Id;
            }
        }

        /// <summary>让 foreach (var (k, v) in dict) 在 net48 上可用。</summary>
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
        {
            key = kv.Key;
            value = kv.Value;
        }
    }
}
