using System.Reflection;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesPlugin.Modules;
using RetakesPlugin.Modules.Allocators;
using RetakesPlugin.Modules.Config;
using RetakesPlugin.Modules.Managers;
using Helpers = RetakesPlugin.Modules.Helpers;

namespace RetakesPlugin;

[MinimumApiVersion(129)]
public class RetakesPlugin : BasePlugin
{
    public override string ModuleName => "Retakes Plugin";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "B3none";
    public override string ModuleDescription => "Community retakes for CS2.";

    // Constants
    // TODO: Add colours for message prefix.
    public const string MessagePrefix = "[Retakes] ";
    
    // Config
    private MapConfig? _mapConfig;
    
    // State
    private static CCSGameRules? _gameRules;
    private Bombsite _currentBombsite = Bombsite.A;
    private Game _gameManager = new();
    private CCSPlayerController? _planter;
    private readonly Random _random = new();
    private bool _didTerroristsWinLastRound = false;

    public override void Load(bool hotReload)
    {
        Console.WriteLine($"{MessagePrefix}Plugin loaded!");
        
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        if (hotReload)
        {
            // If a hot reload is detected restart the current map.
            Server.ExecuteCommand($"map {Server.MapName}");
        }
    }
    
    // Commands
    [ConsoleCommand("css_addspawn", "Adds a spawn point for retakes to the map.")]
    [CommandHelper(minArgs: 2, usage: "[T/CT] [A/B] [Y/N (can be planter / optional)]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void AddSpawnCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!Helpers.CanPlayerAddSpawn(player))
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must be a player.");
            return;
        }
        
        var team = commandInfo.GetArg(1).ToUpper();
        if (team != "T" && team != "CT")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a team [T / CT] - [Value: {team}].");
            return;
        }
        
        var bombsite = commandInfo.GetArg(2).ToUpper();
        if (bombsite != "A" && bombsite != "B")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}You must specify a bombsite [A / B] - [Value: {bombsite}].");
            return;
        }

        var canBePlanter = commandInfo.GetArg(3).ToUpper();
        if (canBePlanter != "" && canBePlanter != "Y" && canBePlanter != "N")
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Invalid value passed to can be a planter [Y / N] - [Value: {canBePlanter}].");
            return;
        }

        var spawn = new Spawn(
            vector: player!.PlayerPawn.Value!.AbsOrigin!,
            qAngle: player!.PlayerPawn.Value!.AbsRotation!
        )
        {
            Team = team == "T" ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
            CanBePlanter = team == "T" && canBePlanter == "Y",
            Bombsite = bombsite == "A" ? Bombsite.A : Bombsite.B
        };

        if (_mapConfig == null)
        {
            commandInfo.ReplyToCommand($"{MessagePrefix}Map config not loaded for some reason...");
            return;
        }
        
        var didAddSpawn = _mapConfig.AddSpawn(spawn);
        
        commandInfo.ReplyToCommand($"{MessagePrefix}{(didAddSpawn ? "Spawn added" : "Error adding spawn")}");
    }
    
    [ConsoleCommand("css_teleport", "This command teleports the player to the given coordinates")]
    [RequiresPermissions("@css/root")]
    public void OnCommandTeleport(CCSPlayerController? player, CommandInfo command)
    {
        if (player == null)
        {
            return;
        }
        if (!player.PlayerPawn.IsValid)
        {
            return;
        }

        
        if (command.ArgCount != 4)
        {
            return;
        }

        if (!float.TryParse(command.ArgByIndex(1), out float positionX))
        {
            return;
        }

        if (!float.TryParse(command.ArgByIndex(2), out float positionY))
        {
            return;
        }

        if (!float.TryParse(command.ArgByIndex(3), out float positionZ))
        {
            return;
        }

        player?.PlayerPawn?.Value?.Teleport(new Vector(positionX, positionY, positionZ), new QAngle(0f,0f,0f), new Vector(0f, 0f, 0f));
    }
    
    // Listeners
    private void OnMapStart(string mapName)
    {
        Console.WriteLine($"{MessagePrefix}OnMapStart listener triggered!");
        
        // Execute the retakes configuration.
        Helpers.ExecuteRetakesConfiguration();
        
        // If we don't have a map config loaded, load it.
        if (_mapConfig == null || _mapConfig.MapName != Server.MapName)
        {
            _mapConfig = new MapConfig(ModuleDirectory, Server.MapName);
            _mapConfig.Load();
        }
    }
    
    [GameEventHandler]
    public HookResult OnRoundPreStart(EventRoundPrestart @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}Round Pre Start event fired!");

        // If we don't have the game rules, get them.
        _gameRules = Helpers.GetGameRules();
        
        if (_gameRules == null)
        {
            Console.WriteLine($"{MessagePrefix}Game rules not found.");
            return HookResult.Continue;
        }
        
        // If we are in warmup, skip.
        if (_gameRules.WarmupPeriod)
        {
            Console.WriteLine($"{MessagePrefix}Warmup round, skipping.");
            return HookResult.Continue;
        }
        
        if (!_gameManager.Queue.ActivePlayers.Any())
        {
            Console.WriteLine($"{MessagePrefix}No active players, skipping.");
            _gameManager.SetupActivePlayers();
            return HookResult.Continue;
        }
        
        // Update Queue status
        _gameManager.Queue.DebugQueues(true);
        _gameManager.Queue.Update();
        _gameManager.Queue.DebugQueues(false);
        
        // Handle team swaps at the start of the round
        if (_didTerroristsWinLastRound)
        {
            _gameManager.TerroristRoundWin();
        }
        else
        {
            _gameManager.CounterTerroristRoundWin();
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}Round Start event fired!");

        // If we don't have the game rules, get them.
        _gameRules = Helpers.GetGameRules();
        
        if (_gameRules == null)
        {
            Console.WriteLine($"{MessagePrefix}Game rules not found.");
            return HookResult.Continue;
        }
        
        // If we are in warmup, skip.
        if (_gameRules.WarmupPeriod)
        {
            Console.WriteLine($"{MessagePrefix}Warmup round, skipping.");
            return HookResult.Continue;
        }
        
        // Reset round state.
        _currentBombsite = _random.Next(0, 2) == 0 ? Bombsite.A : Bombsite.B;
        _planter = null;
        _gameManager.ResetPlayerScores();
        
        // TODO: Cache the spawns so we don't have to do this every round.
        // Filter the spawns.
        List<Spawn> tSpawns = new();
        List<Spawn> ctSpawns = new();
        foreach (var spawn in Helpers.Shuffle(_mapConfig!.GetSpawnsClone()))
        {
            if (spawn.Bombsite != _currentBombsite)
            {
                continue;
            }
            
            switch (spawn.Team)
            {
                case CsTeam.Terrorist:
                    tSpawns.Add(spawn);
                    break;
                case CsTeam.CounterTerrorist:
                    ctSpawns.Add(spawn);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        
        Console.WriteLine($"{MessagePrefix}There are {tSpawns.Count} Terrorist, and {ctSpawns.Count} Counter-Terrorist spawns available for bombsite {(_currentBombsite == Bombsite.A ? "A" : "B")}.");
        Server.PrintToChatAll($"{MessagePrefix}There are {tSpawns.Count} Terrorist, and {ctSpawns.Count} Counter-Terrorist spawns available for bombsite {(_currentBombsite == Bombsite.A ? "A" : "B")}.");
        
        Console.WriteLine($"{MessagePrefix}Moving players to spawns.");
        // Now move the players to their spawns.
        // We shuffle this list to ensure that 1 player does not have to plant every round.
        foreach (var player in Helpers.Shuffle(_gameManager.Queue.ActivePlayers))
        {
            if (!Helpers.IsValidPlayer(player) || player.TeamNum < (int)CsTeam.Terrorist)
            {
                continue;
            }
            
            var playerPawn = player.PlayerPawn.Value;

            if (playerPawn == null)
            {
                continue;
            }
            
            var isTerrorist = player.TeamNum == (byte)CsTeam.Terrorist;

            Spawn spawn;
            
            if (_planter == null && isTerrorist)
            {
                _planter = player;
                
                var spawnIndex = tSpawns.FindIndex(tSpawn => tSpawn.CanBePlanter);

                if (spawnIndex == -1)
                {
                    Console.WriteLine($"{MessagePrefix}No bomb planter spawn found in configuration.");
                    throw new Exception("No bomb planter spawn found in configuration.");
                }
                
                spawn = tSpawns[spawnIndex];
                
                tSpawns.RemoveAt(spawnIndex);
            }
            else
            {
                Console.WriteLine($"{MessagePrefix}GetAndRemoveRandomItem called.");
                spawn = Helpers.GetAndRemoveRandomItem(isTerrorist ? tSpawns : ctSpawns);
                Console.WriteLine($"{MessagePrefix}GetAndRemoveRandomItem complete.");
            }
            
            playerPawn.Teleport(spawn.Vector, spawn.QAngle, new Vector());
        }
        Console.WriteLine($"{MessagePrefix}Moving players to spawns COMPLETE.");
        
        Console.WriteLine($"{MessagePrefix}Printing bombsite output to all players.");
        Server.PrintToChatAll($"{MessagePrefix}Bombsite: {(_currentBombsite == Bombsite.A ? "A" : "B")}");
        Console.WriteLine($"{MessagePrefix}Printing bombsite output to all players COMPLETE.");
        
        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnRoundPostStart(EventRoundPoststart @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}OnRoundPostStart event fired.");

        foreach (var player in _gameManager.Queue.ActivePlayers)
        {
            // Strip the player of all of their weapons and the bomb before any spawn / allocation occurs.
            // TODO: Figure out why this is crashing the server / undo workaround.
            // player.RemoveWeapons();
            Helpers.RemoveAllItemsAndEntities(player);
            
            // Create a timer to do this as it would occasionally fire too early.
            AddTimer(0.05f, () =>
            {
                Weapons.Allocate(player);
                Equipment.Allocate(player, player == _planter);
                Grenades.Allocate(player);
            });
        }

        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}OnFreezeTimeEnd event fired.");
        var pBombCarrierController = Helpers.GetBombCarrier();

        if (pBombCarrierController == null)
        {
            Console.WriteLine($"{MessagePrefix}Bomb carrier not found.");
            return HookResult.Continue;
        }

        if (!pBombCarrierController.PlayerPawn.Value!.InBombZone)
        {
            Console.WriteLine($"{MessagePrefix}Bomb carrier not in bomb zone.");
            return HookResult.Continue;
        }

        Console.WriteLine($"{MessagePrefix}Planting c4...");
        CreatePlantedC4(pBombCarrierController);
        Console.WriteLine($"{MessagePrefix}Planting c4 DONE");

        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}OnPlayerSpawn event fired.");
        
        // If we don't have the game rules, get them.
        _gameRules = Helpers.GetGameRules();
        
        if (_gameRules == null)
        {
            Console.WriteLine($"{MessagePrefix}Game rules not found.");
            return HookResult.Continue;
        }
        
        // If we are in warmup, skip.
        if (_gameRules.WarmupPeriod)
        {
            Console.WriteLine($"{MessagePrefix}Warmup round, skipping.");
            return HookResult.Continue;
        }
        
        var player = @event.Userid;

        if (!Helpers.IsValidPlayer(player) && Helpers.IsPlayerConnected(player))
        {
            return HookResult.Continue;
        }
        
        // debug and check if the player is in the queue.
        Console.WriteLine($"{MessagePrefix}[{player.PlayerName}] Checking ActivePlayers.");
        if (!_gameManager.Queue.ActivePlayers.Contains(player))
        {
            Console.WriteLine($"{MessagePrefix}[{player.PlayerName}] Player not in ActivePlayers, moving to spectator.");
            if (!player.IsBot)
            {
                player.ChangeTeam(CsTeam.Spectator);
            }

            if (player.PlayerPawn.Value != null)
            {
                player.PlayerPawn.Value.Health = 0;
                player.PlayerPawn.Value.Remove();
            }
            return HookResult.Continue;
        }
        else
        {
            Console.WriteLine($"{MessagePrefix}[{player.PlayerName}] Player is in ActivePlayers.");
        }

        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;

        if (!Helpers.IsValidPlayer(player))
        {
            return HookResult.Continue;
        }
        
        player.TeamNum = (int)CsTeam.Spectator;
        player.ForceTeamTime = 3600.0f;

        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var assister = @event.Assister;

        if (!Helpers.IsValidPlayer(attacker))
        {
            return HookResult.Continue;
        }
        
        _gameManager.AddScore(attacker, Game.ScoreForKill);
        _gameManager.AddScore(assister, Game.ScoreForAssist);

        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}OnBombPlanted event fired");

        // If we don't have the game rules, get them.
        _gameRules = Helpers.GetGameRules();
        
        if (_gameRules == null)
        {
            Console.WriteLine($"{MessagePrefix}Game rules not found.");
            return HookResult.Continue;
        }
        
        // Get planted c4
        var plantedC4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4").FirstOrDefault();
        
        if (plantedC4 == null)
        {
            Console.WriteLine($"{MessagePrefix}Planted C4 not found.");
            return HookResult.Continue;
        }
        
        plantedC4.C4Blow = Server.CurrentTime + 40.0f;
        
        // set game rules
        Console.WriteLine($"{MessagePrefix}setting game rules");
        _gameRules.BombDropped = false;
        _gameRules.BombPlanted = true;
        _gameRules.BombDefused = false;
        _gameRules.RetakeRules.BlockersPresent = false;
        _gameRules.RetakeRules.RoundInProgress = true;
        _gameRules.RetakeRules.BombSite = plantedC4.BombSite;
        
        // Debug planted c4
        List<string> c4NestedProps = new() { "" };
        Console.WriteLine("");
        Console.WriteLine("Planted C4 Props...");
        Helpers.DebugObject("planted_c4", plantedC4, c4NestedProps);
        
        List<string> gameRulesNestedProps = new() { "RetakeRules" };
        Console.WriteLine("");
        Console.WriteLine("Game Rules Props...");
        Helpers.DebugObject("_gameRules", _gameRules, gameRulesNestedProps);
        
        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}On Bomb Begin Defuse event fired.");

        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}On Bomb Defused event fired.");
        
        var player = @event.Userid;

        if (!Helpers.IsValidPlayer(player))
        {
            return HookResult.Continue;
        }
        
        _gameManager.AddScore(player, Game.ScoreForDefuse);

        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}OnRoundEnd event fired.");
        
        // If we don't have the game rules, get them.
        var gameRules = Helpers.GetGameRules();
        
        if (gameRules == null)
        {
            Console.WriteLine($"{MessagePrefix}Game rules not found.");
            return HookResult.Continue;
        }
        
        gameRules.BombPlanted = false;

        _didTerroristsWinLastRound = @event.Winner == (int)CsTeam.Terrorist;

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}[{@event.Userid.PlayerName}] OnPlayerTeam event fired. ({(@event.Isbot ? "BOT" : "NOT BOT")}) {(CsTeam)@event.Oldteam} -> {(CsTeam)@event.Team}");
        
        _gameManager.Queue.DebugQueues(true);
        _gameManager.Queue.PlayerTriedToJoinTeam(@event.Userid, @event.Team != (int)CsTeam.Spectator);
        _gameManager.Queue.DebugQueues(false);

        return HookResult.Continue;
    }
    
    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        Console.WriteLine($"{MessagePrefix}OnPlayerDisconnect event fired.");

        _gameManager.Queue.DebugQueues(true);
        _gameManager.Queue.PlayerDisconnected(@event.Userid);
        _gameManager.Queue.DebugQueues(false);

        return HookResult.Continue;
    }
    
    // Autoplant helpers (credit zwolof)
    private bool CreatePlantedC4(CCSPlayerController bombCarrier)
    {
        Console.WriteLine($"{MessagePrefix}removing bomb");
        Helpers.RemoveItemByDesignerName(bombCarrier, "weapon_c4");
        
        Console.WriteLine($"{MessagePrefix}1");
        var gameRules = Helpers.GetGameRules();
        Console.WriteLine($"{MessagePrefix}2");

        if (gameRules == null || !Helpers.IsValidPlayer(bombCarrier))
        {
            Console.WriteLine($"{MessagePrefix}return false 1");
            return false;
        }
        
        var plantedC4 = Utilities.CreateEntityByName<CPlantedC4>("planted_c4");

        if (plantedC4 == null)
        {
            Console.WriteLine($"{MessagePrefix}return false 2");
            return false;
        }

        var playerOrigin = bombCarrier.PlayerPawn.Value!.AbsOrigin;

        if (playerOrigin == null)
        {
            Console.WriteLine($"{MessagePrefix}return false 3");
            return false;
        }
        
        playerOrigin.Z -= bombCarrier.PlayerPawn.Value.Collision.Mins.Z;
        
        Console.WriteLine($"{MessagePrefix}setting planted c4 props");
        plantedC4.BombTicking = true;
        plantedC4.CannotBeDefused = false;
        plantedC4.BeingDefused = false;
        plantedC4.SourceSoundscapeHash = 2005810340;
        
        if (_planter != null)
        {
            Console.WriteLine($"{MessagePrefix}Setting CPlantedC4 m_hOwnerEntity");
            Schema.SetSchemaValue(plantedC4.Handle, "CBaseEntity", "m_hOwnerEntity", _planter.Index);
        }

        Console.WriteLine($"{MessagePrefix}calling dispatch spawn");
        plantedC4.DispatchSpawn();
        
        Console.WriteLine($"{MessagePrefix}complete! waiting for next frame");
        
        Server.NextFrame(() =>
        {
            Console.WriteLine($"{MessagePrefix}teleporting prop");
            plantedC4.Teleport(playerOrigin, new QAngle(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), new Vector(0, 0, 0));

            Console.WriteLine(
                $"{MessagePrefix}getting bombtargets");
            var bombTargets =
                Utilities.FindAllEntitiesByDesignerName<CBombTarget>("func_bomb_target").ToList();

            if (bombTargets.Any())
            {
                Console.WriteLine($"{MessagePrefix}got bomb targets, setting bombplantedhere");

                bombTargets.Where(bombTarget => bombTarget.IsBombSiteB == (_currentBombsite == Bombsite.B))
                    .ToList()
                    .ForEach(bombTarget =>
                    {
                        Console.WriteLine($"{MessagePrefix}actually setting BombPlantedHere for {bombTarget.DesignerName}");
                        bombTarget.BombPlantedHere = true;
                    });
            }

            Console.WriteLine($"{MessagePrefix}sending bomb planted event");
            SendBombPlantedEvent(bombCarrier, plantedC4);

            Console.WriteLine($"{MessagePrefix}setting ct playerPawn properties");
            foreach (var player in Utilities.GetPlayers().Where(player => player.TeamNum == (int)CsTeam.CounterTerrorist))
            {
                if (player.PlayerPawn.Value == null)
                {
                    continue;
                }

                Console.WriteLine($"{MessagePrefix} setting for {player.PlayerName}");
                player.PlayerPawn.Value.RetakesHasDefuseKit = true;
                player.PlayerPawn.Value.IsDefusing = false;
                player.PlayerPawn.Value.LastGivenDefuserTime = 0.0f;
                player.PlayerPawn.Value.InNoDefuseArea = false;
            }
        });

        return true;
    }

    private void SendBombPlantedEvent(CCSPlayerController bombCarrier, CPlantedC4 plantedC4)
    {
        if (bombCarrier.PlayerPawn.Value == null)
        {
            return;
        }

        Console.WriteLine($"{MessagePrefix}Creating event");
        var bombPlantedEvent = NativeAPI.CreateEvent("bomb_planted", true);
        Console.WriteLine($"{MessagePrefix}Setting player controller handle");
        NativeAPI.SetEventPlayerController(bombPlantedEvent, "userid", bombCarrier.Handle);
        
        Console.WriteLine($"{MessagePrefix}Setting userid");
        NativeAPI.SetEventInt(bombPlantedEvent, "userid", (int)bombCarrier.PlayerPawn.Value.Index);
        
        Console.WriteLine($"{MessagePrefix}Setting posx to {bombCarrier.PlayerPawn.Value.AbsOrigin!.X}");
        NativeAPI.SetEventFloat(bombPlantedEvent, "posx", bombCarrier.PlayerPawn.Value.AbsOrigin!.X);
        
        Console.WriteLine($"{MessagePrefix}Setting posy to {bombCarrier.PlayerPawn.Value.AbsOrigin!.Y}");
        NativeAPI.SetEventFloat(bombPlantedEvent, "posy", bombCarrier.PlayerPawn.Value.AbsOrigin!.Y);
        
        Console.WriteLine($"{MessagePrefix}Setting site");
        NativeAPI.SetEventInt(bombPlantedEvent, "site", plantedC4.BombSite);
        
        Console.WriteLine($"{MessagePrefix}Setting priority");
        NativeAPI.SetEventInt(bombPlantedEvent, "priority", 5);

        NativeAPI.FireEvent(bombPlantedEvent, false);
    }
}
