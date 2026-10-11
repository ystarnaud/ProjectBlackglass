# Combat audio placeholders

Every `PLACEHOLDER_*.wav` in this folder is generated locally by `Blackglass/Presentation/Build Effects`
(`PlaceholderSounds.cs`): filtered noise bursts and decaying sine tones with fixed seeds. They are original
to this project, there is no external audio and no third-party licence, and they are meant to be replaced.

To replace one, import a clip and assign it in the `AudioCue` of the relevant `CombatPresentationProfile`
(`Assets/_Project/Presentation/Profiles`). Nothing in code names these files.
