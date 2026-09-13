// `init` accessors are a compiler feature, not a runtime one: the compiler only requires that
// System.Runtime.CompilerServices.IsExternalInit exists somewhere. .NET 5 and later ship it;
// netstandard2.1, which this mod targets so one DLL serves both game branches, does not. Declaring
// it here is the documented way to use init on that target and costs nothing at runtime.
//
// It exists so FlightTime.SolveInput can be built from a named object initializer instead of a
// positional constructor. That struct carries 65 values, 30-odd of them float or bool, and every
// field added to it used to arrive as one more trailing optional parameter whose default carried
// its meaning in a comment rather than in a name.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
