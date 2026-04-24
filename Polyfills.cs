// Required polyfill: C# 9+ record types depend on IsExternalInit, which only
// ships in .NET 5+. Defining it here makes records compile on net472 (the
// .NET Framework target used by VS 2026's classic MEF extension host).
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
