using System.Runtime.CompilerServices;

// Tests wire components through internal Initialize methods instead of reflection.
[assembly: InternalsVisibleTo("Blackglass.Tests.EditMode")]
[assembly: InternalsVisibleTo("Blackglass.Tests.PlayMode")]

// Editor tooling (the item and scene builders under Assets/_Project/Editor, such as InventoryDataBuilder and
// InventorySceneBuilder) compiles into Assembly-CSharp-Editor. This grant exposes every internal of the game assembly
// to all editor scripts, so the builders can use the same internal Initialize methods the tests use.
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
