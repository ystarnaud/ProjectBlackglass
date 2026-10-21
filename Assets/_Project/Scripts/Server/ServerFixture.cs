namespace Blackglass
{
    /// <summary>
    /// The dedicated server's fixture mission (Checkpoint A, decision 045): the settings the server scene's director holds and
    /// the attempt fixtures are generated with. Runtime code so the scene builder, the attempt fixture export and the tests
    /// share one definition.
    /// </summary>
    public static class ServerFixture
    {
        /// <summary>The fixture seed: the lowest seed in 1..1000 that generates on attempt 1 and passes the Editor diagnostic.</summary>
        public const int Seed = 1;

        /// <summary>The small fixture mission: 2x2 grid, 3 rooms, two friendlies, one hostile, no terminal, no guards.</summary>
        public static MissionSettings Settings() => new MissionSettings
        {
            seed = Seed,
            gridColumns = 2,
            gridRows = 2,
            cellSize = 14,
            roomCount = 3,
            corridorWidth = 3,
            extraLoops = 0,
            bafflesPerRoom = 1,
            lowCoverDensity = 3f,
            friendlyCount = 2,
            hostileCount = 1,
            minTeamSeparation = 16f,
            maxAttempts = 20,
            guardCount = 0,
            interactionSeconds = 2f,
            extractionUnits = 1,
            eliminateHostiles = true,
            hackTerminal = false,
            // Fog on: the networked server runs the real knowledge model and visibility fails closed without it (decision 047).
            // Map and objectives stay known; no briefing markers and no cameras, so only sight and exposure reveal a hostile.
            intelligence = new IntelligenceSettings
            {
                fogEnabled = true,
                map = MapKnowledge.Full,
                objectives = ObjectiveKnowledge.All,
                enemyMarkersAtStart = 0,
                cameraCount = 0,
            },
        };
    }
}
