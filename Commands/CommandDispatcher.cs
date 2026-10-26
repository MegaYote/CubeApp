using System.Globalization;

namespace Cubuild
{
    internal readonly record struct CommandResponse(bool Success, string Message)
    {
        public static CommandResponse Ok(string message) => new(true, message);
        public static CommandResponse Error(string message) => new(false, message);
    }

    internal sealed record CommandContext(
        GameWorld World, bool IsMultiplayerClient, bool IsMultiplayer, double WorldBoundaryLimit);

    /// <summary>Small command registry. New slash commands can be added without growing input handling.</summary>
    internal sealed class CommandDispatcher
    {
        private readonly Dictionary<string, Func<CommandContext, string[], CommandResponse>> _commands
            = new(StringComparer.OrdinalIgnoreCase);

        public CommandDispatcher()
        {
            Register("tp", Teleport);
            Register("worldedge", WorldEdge);
        }

        private void Register(string name, Func<CommandContext, string[], CommandResponse> handler)
            => _commands.Add(name, handler);

        public CommandResponse Execute(string text, CommandContext? context)
        {
            if (string.IsNullOrWhiteSpace(text))
                return CommandResponse.Error("Enter a command.");

            string[] tokens = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens[0][0] != '/')
                return CommandResponse.Error("Commands must start with '/'.");
            if (tokens[0].Length < 2)
                return CommandResponse.Error("Usage: /tp x y z (use exactly '~' for terrain height).");

            string name = tokens[0][1..];
            if (!_commands.TryGetValue(name, out var handler))
                return CommandResponse.Error($"Unknown command '/{name}'. Available: /tp x y z, /worldedge.");
            if (context == null)
                return CommandResponse.Error("There is no active world.");

            var arguments = new string[tokens.Length - 1];
            Array.Copy(tokens, 1, arguments, 0, arguments.Length);
            return handler(context, arguments);
        }

        private static CommandResponse Teleport(CommandContext context, string[] arguments)
        {
            if (context.IsMultiplayerClient)
                return CommandResponse.Error("Teleport commands are unsupported on multiplayer clients; local teleporting would desynchronize the world.");
            if (arguments.Length != 3)
                return CommandResponse.Error("Usage: /tp x y z (use exactly '~' for terrain height).");

            if (!TryParseFinite(arguments[0], out double x) || !TryParseFinite(arguments[2], out double z))
                return CommandResponse.Error("X and Z must be finite invariant-culture coordinates.");

            if (string.Equals(arguments[1], "~", StringComparison.Ordinal))
            {
                if (!context.World.TryTeleportPlayerToTerrain(x, z, out string error))
                    return CommandResponse.Error(error);
                return CommandResponse.Ok(FormattableString.Invariant(
                    $"Teleported to verified terrain at X {x:0.###}, Z {z:0.###}."));
            }

            if (!TryParseFinite(arguments[1], out double feetY))
                return CommandResponse.Error("Y must be a finite feet height or exactly '~'.");
            if (!context.World.TryTeleportPlayerToFeet(x, feetY, z, out string teleportError))
                return CommandResponse.Error(teleportError);

            return CommandResponse.Ok(FormattableString.Invariant(
                $"Teleported to X {x:0.###}, feet Y {feetY:0.###}, Z {z:0.###}."));
        }

        private static CommandResponse WorldEdge(CommandContext context, string[] arguments)
        {
            if (context.IsMultiplayer)
                return CommandResponse.Error("World-edge testing is only available in single-player; boundary transitions are disabled in multiplayer.");
            if (arguments.Length != 0)
                return CommandResponse.Error("Usage: /worldedge");

            var position = context.World.PlayerPosition;
            double edge = context.WorldBoundaryLimit;
            double inset = 100.0;

            // Pick the closest horizontal boundary, then clamp the lateral axis too so a corner
            // approach never leaves the player less than the requested 100-block safety margin.
            var candidates = new (double Distance, int Axis, int Sign)[]
            {
                (Math.Max(0.0, edge - position.X), 0, 1),
                (Math.Max(0.0, edge + position.X), 0, -1),
                (Math.Max(0.0, edge - position.Z), 1, 1),
                (Math.Max(0.0, edge + position.Z), 1, -1),
            };
            var nearest = candidates[0];
            for (int i = 1; i < candidates.Length; i++)
                if (candidates[i].Distance < nearest.Distance) nearest = candidates[i];

            double safeEdgeCoordinate = edge - inset;
            double x = Math.Clamp(position.X, -safeEdgeCoordinate, safeEdgeCoordinate);
            double z = Math.Clamp(position.Z, -safeEdgeCoordinate, safeEdgeCoordinate);
            if (nearest.Axis == 0) x = nearest.Sign * safeEdgeCoordinate;
            else z = nearest.Sign * safeEdgeCoordinate;

            if (!context.World.TryTeleportPlayerToTerrain(x, z, out string error))
                return CommandResponse.Error("Could not find safe terrain near the world edge: " + error);

            string side = nearest.Axis == 0
                ? nearest.Sign > 0 ? "+X" : "-X"
                : nearest.Sign > 0 ? "+Z" : "-Z";
            return CommandResponse.Ok(FormattableString.Invariant(
                $"Teleported 100 blocks inside the {side} edge at X {x:0}, Z {z:0}; safe terrain Y {context.World.PlayerPosition.Y - GameWorld.EyeHeight:0.##}."));
        }

        private static bool TryParseFinite(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && double.IsFinite(value);
        }
    }
}
