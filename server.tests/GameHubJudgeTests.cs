using CorroServer.Hubs;
using CorroServer.Models;
using CorroServer.Services;
using CorroServer.Tests.Doubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace CorroServer.Tests;

/// <summary>
/// Naming the judge of a fixed-judge categories table. It lives in the waiting room rather than
/// inside the match on purpose: here "the host" is a recorded fact, while a running game has had
/// its turn order SHUFFLED at start, so its first seat is nobody in particular.
/// </summary>
public class GameHubJudgeTests
{
	private const string GameId = "judge-game";

	private static async Task<(GameHub Hub, FakeClients Clients, InMemoryGameRepository Repository)>
		BuildHarness(string callerPlayerId, GameStatus status = GameStatus.WaitingForPlayers)
	{
		var connectionId = "connection-" + Guid.NewGuid().ToString("N");
		var repository = new InMemoryGameRepository();
		await repository.CreateGameAsync(new GameDocument
		{
			Id = "game-doc-" + GameId,
			GameId = GameId,
			Status = status,
			HostId = "host",
			InviteCode = "JUDGE1",
			GameType = "categories",
			Players = new List<LobbyPlayer>
			{
				new() { Id = "host", Name = "Ada", Token = "quill", IsHost = true, PlayerSecretId = "host-secret" },
				new() { Id = "berto", Name = "Berto", Token = "clock", PlayerSecretId = "berto-secret" },
				new() { Id = "cora", Name = "Cora", Token = "bell", PlayerSecretId = "cora-secret" },
			},
		});

		var timer = new FakeAuctionTimer();
		var registry = new GameSessionRegistry(
			new FakeHubContext(), repository, timer, TestFixtures.NewPackageRestorer());
		registry.MapConnectionToGame(connectionId, GameId);
		registry.AuthenticateConnection(connectionId, callerPlayerId);

		var clients = new FakeClients();
		var hub = new GameHub(
			repository,
			gameServiceFactory: null!,
			timer,
			packageStore: null!,
			packageRestorer: null!,
			registry,
			NullLogger<GameHub>.Instance)
		{
			Clients = clients,
			Context = new FakeCallerContext(connectionId),
			Groups = new FakeGroupManager(),
		};
		return (hub, clients, repository);
	}

	private static AssignJudgeRequest Naming(string? playerId, string hostId = "host")
		=> new() { GameId = GameId, HostId = hostId, PlayerId = playerId };

	[Fact]
	public async Task The_host_names_the_judge_and_the_whole_room_is_told()
	{
		var (hub, clients, repository) = await BuildHarness("host");

		await hub.AssignJudge(Naming("cora"));

		Assert.Equal("cora", (await repository.LoadGameAsync(GameId))!.FixedJudgeId);
		var lobby = clients.Group($"lobby_{GameId}");
		Assert.True(lobby.Received("LobbyUpdated"));
		// Spoken separately from the repaint, the way a team assignment is.
		Assert.True(lobby.Received("JudgeAssigned"));
	}

	[Fact]
	public async Task Naming_nobody_hands_the_job_back_to_the_first_seat()
	{
		var (hub, _, repository) = await BuildHarness("host");
		await hub.AssignJudge(Naming("cora"));

		await hub.AssignJudge(Naming(null));

		Assert.Null((await repository.LoadGameAsync(GameId))!.FixedJudgeId);
	}

	/// <summary>The bug this arrangement exists to prevent: a player who is not the host naming
	/// the referee of everybody else's match.</summary>
	[Fact]
	public async Task A_player_who_is_not_the_host_cannot_name_the_judge()
	{
		var (hub, clients, repository) = await BuildHarness("berto");

		await hub.AssignJudge(Naming("berto", hostId: "berto"));

		Assert.Null((await repository.LoadGameAsync(GameId))!.FixedJudgeId);
		Assert.Equal("HOST_ONLY", clients.Caller.LastError());
	}

	[Fact]
	public async Task Somebody_who_is_not_at_this_table_cannot_be_named()
	{
		var (hub, clients, repository) = await BuildHarness("host");

		await hub.AssignJudge(Naming("nobody"));

		Assert.Null((await repository.LoadGameAsync(GameId))!.FixedJudgeId);
		Assert.Equal("PLAYER_NOT_FOUND", clients.Caller.LastError());
	}

	[Fact]
	public async Task Once_the_match_is_running_the_job_is_not_taken_off_whoever_is_doing_it()
	{
		var (hub, clients, repository) = await BuildHarness("host", GameStatus.Active);

		await hub.AssignJudge(Naming("cora"));

		Assert.Null((await repository.LoadGameAsync(GameId))!.FixedJudgeId);
		Assert.Equal("GAME_ALREADY_STARTED", clients.Caller.LastError());
	}
}
