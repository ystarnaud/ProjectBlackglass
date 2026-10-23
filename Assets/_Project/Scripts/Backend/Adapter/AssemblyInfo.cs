using System.Runtime.CompilerServices;

// The tests reach the credential's test constructor and header builder; nothing else outside this assembly may.
[assembly: InternalsVisibleTo("Blackglass.Tests.EditMode")]
[assembly: InternalsVisibleTo("Blackglass.Tests.PlayMode")]
[assembly: InternalsVisibleTo("Blackglass.Tests.BackendSupport")]
