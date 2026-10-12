#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    // Lets C# 9 records and init-only properties compile on Unity's .NET Standard 2.1 profile.
    internal static class IsExternalInit { }
}
#endif
