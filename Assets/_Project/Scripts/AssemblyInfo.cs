using System.Runtime.CompilerServices;

// Tests wire components through internal Initialize methods instead of reflection.
[assembly: InternalsVisibleTo("Blackglass.Tests.EditMode")]
[assembly: InternalsVisibleTo("Blackglass.Tests.PlayMode")]
