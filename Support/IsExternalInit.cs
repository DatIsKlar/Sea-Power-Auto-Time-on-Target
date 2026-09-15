// `init` accessors only need System.Runtime.CompilerServices.IsExternalInit to exist. netstandard2.1, which
// this mod targets so one DLL serves both game branches, does not ship it; declaring it here is the
// documented workaround and costs nothing at runtime. It lets FlightTime.SolveInput use a named object initializer.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
