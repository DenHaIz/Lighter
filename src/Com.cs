using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Lighter
{
    /// <summary>
    /// Minimal late-bound COM access through IDispatch. Replaces C# "dynamic", which would pull
    /// Microsoft.CSharp and the DLR binder (megabytes of memory and JIT) into the process.
    /// </summary>
    static class Com
    {
        public static object Create(string progId)
        {
            return Activator.CreateInstance(Type.GetTypeFromProgID(progId, true));
        }

        public static object Get(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, args);
        }

        public static void Set(object o, string name, object value)
        {
            o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });
        }

        public static object Call(object o, string name, params object[] args)
        {
            return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        }

        /// <summary>Items of a 1-based COM collection (Count / Item).</summary>
        public static IEnumerable<object> Items(object collection)
        {
            int count = Convert.ToInt32(Get(collection, "Count"));
            for (int i = 1; i <= count; i++) yield return Get(collection, "Item", i);
        }

        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }
}
