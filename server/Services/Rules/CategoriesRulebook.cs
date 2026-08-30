using CorroServer.Models;
using CorroServer.Models.Corro;

namespace CorroServer.Services.Rules;

/// <summary>
/// Pure state transitions for the categories family. Everything a human decides stays with the
/// human: the rulebook gathers identical answers and checks the round's letter, but it only ever
/// offers those as HINTS. One rotating judge rules, and the score follows the ruling.
/// </summary>
public static class CategoriesRulebook
{
	public sealed record RoundAdvance(bool GameOver, string? WinnerId, bool TieBreakerStarted);

	/// <summary>One player's take from a scored round, for the round's spoken result lines.</summary>
	public sealed record RoundScore(string PlayerId, int Points, int Total);

	/// <param name="firstJudgeId">Who judges from the start: under a fixed judge it is the player
	/// the host named in the waiting room, and it never changes again. Null — or a seat that is not
	/// at this table — falls back to the first seat, so a table that named nobody still plays.</param>
	public static CategoriesState CreateInitialState(
		IReadOnlyList<string> playerIds,
		CategoryDeckDef deck,
		CategoriesRulesConfig rules,
		string? firstJudgeId = null)
	{
		if (playerIds.Count < 3)
		{
			throw new InvalidOperationException("categories games need at least three players: a judge and two writers.");
		}
		if (deck.Categories.Count < rules.CategoriesPerRound)
		{
			throw new InvalidOperationException("categories deck has fewer categories than one round deals.");
		}
		if (deck.Letters.Count == 0)
		{
			throw new InvalidOperationException("categories deck offers no letters.");
		}

		var state = new CategoriesState
		{
			Players = playerIds.Select(id => new CategoriesPlayerState { PlayerId = id }).ToList(),
			// Replaced on the next line. The first deal reads the state it is dealt into (the
			// deck cursors live there), so the round cannot exist before the state does.
			Round = null!,
		};
		var judgeId = firstJudgeId is not null && playerIds.Contains(firstJudgeId)
			? firstJudgeId
			: playerIds[0];
		state.Round = DealRound(state, deck, rules, roundNumber: 1, judgeId);
		return state;
	}

	/// <summary>Whether this match has one judge for its whole length rather than a rotating one.</summary>
	public static bool HasFixedJudge(CategoriesRulesConfig rules) => rules.JudgeMode == "fixed";

	/// <summary>Everyone who is playing FOR the win. Under a fixed judge that is everybody but the
	/// judge: they never write, so they can never score, and leaving them in the running would
	/// mean a match decided in favour of somebody who was refereeing it.</summary>
	public static IEnumerable<CategoriesPlayerState> Contenders(
		CategoriesState state,
		CategoriesRulesConfig rules)
		=> HasFixedJudge(rules)
			? state.Players.Where(player => player.PlayerId != state.Round.JudgeId)
			: state.Players;

	/// <summary>The next seat in order among players still in the game. Under a rotating judge
	/// that is who judges next, and rotating fairly IS the fairness of the whole match; under a
	/// fixed one it is only ever used to replace a judge who left (see
	/// <see cref="CompleteRound"/>).</summary>
	public static string NextJudge(CategoriesState state, IReadOnlyList<string> seatOrder, string currentJudgeId)
	{
		var eligible = seatOrder.Where(id => state.Players.Any(player => player.PlayerId == id)).ToList();
		if (eligible.Count == 0)
		{
			throw new InvalidOperationException("categories game has no players left to judge.");
		}
		var index = eligible.IndexOf(currentJudgeId);
		return eligible[(index + 1) % eligible.Count];
	}

	/// <summary>Deal the next round: the next letter, the next categories, one blank answer per
	/// writer. Both cursors wrap, so a long match reuses the deck rather than running dry.</summary>
	public static CategoriesRoundState DealRound(
		CategoriesState state,
		CategoryDeckDef deck,
		CategoriesRulesConfig rules,
		int roundNumber,
		string judgeId)
	{
		var letter = deck.Letters[state.LetterCursor % deck.Letters.Count].ToUpperInvariant();
		state.LetterCursor++;

		var prompts = new List<CategoriesPromptState>();
		for (var i = 0; i < rules.CategoriesPerRound; i++)
		{
			var category = deck.Categories[state.CategoryCursor % deck.Categories.Count];
			state.CategoryCursor++;
			prompts.Add(new CategoriesPromptState
			{
				CategoryId = category.Id,
				Name = category.Name,
				Answers = state.Players
					.Where(player => player.PlayerId != judgeId)
					.Select(player => new CategoriesAnswerState { PlayerId = player.PlayerId })
					.ToList(),
			});
		}

		return new CategoriesRoundState
		{
			RoundNumber = roundNumber,
			JudgeId = judgeId,
			Letter = letter,
			DurationSeconds = rules.RoundSeconds,
			Prompts = prompts,
		};
	}

	public static bool IsExpired(CategoriesRoundState round, DateTime nowUtc)
		=> round.Phase == CategoriesRoundPhase.Writing
			&& round.StartedAt is { } started
			&& nowUtc >= started.AddSeconds(round.DurationSeconds);

	/// <summary>Everyone who is expected to write this round (that is, everyone but the judge).</summary>
	public static IReadOnlyList<string> Writers(CategoriesState state, CategoriesRoundState round)
		=> state.Players.Select(player => player.PlayerId).Where(id => id != round.JudgeId).ToList();

	/// <summary>Store (or replace) one player's answer to one category. Returns false when the
	/// category does not belong to this round or the player is not writing in it.</summary>
	public static bool WriteAnswer(CategoriesRoundState round, string playerId, string categoryId, string text)
	{
		var answer = round.Prompts
			.FirstOrDefault(prompt => prompt.CategoryId == categoryId)?.Answers
			.FirstOrDefault(entry => entry.PlayerId == playerId);
		if (answer is null)
		{
			return false;
		}
		answer.Text = text.Trim();
		return true;
	}

	/// <summary>Close the writing phase and prepare the judge's review: every answer gets its two
	/// mechanical hints, and the ones that need no human — nobody wrote anything — are already
	/// rejected so the judge is never asked to rule on a blank.</summary>
	public static void BeginReview(CategoriesRoundState round)
	{
		round.Phase = CategoriesRoundPhase.Review;
		round.StartedAt = null;
		round.ReviewIndex = 0;
		foreach (var prompt in round.Prompts)
		{
			var groupOf = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var coincidence in prompt.Answers
				.Select(answer => AnswerText.Normalize(answer.Text))
				.Where(normalized => normalized.Length > 0)
				.GroupBy(normalized => normalized, StringComparer.Ordinal)
				.Where(group => group.Count() > 1))
			{
				groupOf[coincidence.Key] = groupOf.Count;
			}

			foreach (var answer in prompt.Answers)
			{
				var normalized = AnswerText.Normalize(answer.Text);
				answer.MatchesLetter = AnswerText.StartsWithLetter(answer.Text, round.Letter);
				answer.DuplicateGroup = groupOf.TryGetValue(normalized, out var group) ? group : -1;
				answer.Verdict = normalized.Length == 0
					? CategoriesVerdict.Rejected
					: CategoriesVerdict.Pending;
			}
		}
	}

	/// <summary>Apply the judge's rulings to one category. Blank answers keep their automatic
	/// rejection: a ruling that tried to accept one is ignored rather than paying for nothing.</summary>
	public static void RulePrompt(
		CategoriesPromptState prompt,
		IReadOnlyDictionary<string, CategoriesVerdict> rulings)
	{
		foreach (var answer in prompt.Answers)
		{
			if (AnswerText.Normalize(answer.Text).Length == 0)
			{
				answer.Verdict = CategoriesVerdict.Rejected;
				continue;
			}
			answer.Verdict = rulings.TryGetValue(answer.PlayerId, out var verdict) && verdict != CategoriesVerdict.Pending
				? verdict
				: CategoriesVerdict.Rejected;
		}
	}

	/// <summary>The verdicts a judge would reach by simply agreeing with every hint: blanks and
	/// wrong initials rejected, coincidences marked repeated, everything else accepted. The client
	/// pre-fills its review controls with these, so ruling a clean category is one confirmation
	/// rather than one decision per player.</summary>
	public static Dictionary<string, CategoriesVerdict> SuggestedRulings(CategoriesPromptState prompt)
		=> prompt.Answers.ToDictionary(
			answer => answer.PlayerId,
			answer => AnswerText.Normalize(answer.Text).Length == 0 || !answer.MatchesLetter
				? CategoriesVerdict.Rejected
				: answer.DuplicateGroup >= 0
					? CategoriesVerdict.Duplicate
					: CategoriesVerdict.Accepted,
			StringComparer.Ordinal);

	/// <summary>Add the round's accepted answers to the running totals, once every category has
	/// been ruled. Returns one line's worth of result per writer, in seat order.</summary>
	public static IReadOnlyList<RoundScore> ScoreRound(
		CategoriesState state,
		CategoriesRoundState round,
		CategoriesRulesConfig rules)
	{
		var scores = new List<RoundScore>();
		foreach (var writerId in Writers(state, round))
		{
			var accepted = round.Prompts
				.SelectMany(prompt => prompt.Answers)
				.Count(answer => answer.PlayerId == writerId && answer.Verdict == CategoriesVerdict.Accepted);
			var player = state.Players.First(entry => entry.PlayerId == writerId);
			player.Score += accepted * rules.PointsPerAnswer;
			scores.Add(new RoundScore(writerId, accepted * rules.PointsPerAnswer, player.Score));
		}
		return scores;
	}

	/// <summary>
	/// Close the scored round and either deal the next one or decide the match. A tie adds one
	/// more of whatever the unit is, so the winner always wins outright.
	///
	/// Both endings are read at the SAME moment: once every judge of this match has taken the
	/// round they were owed. Playing to a target score is not a race to cross it first — the judge
	/// of a round does not write in it, so with a rotating judge one player has always written
	/// more rounds than another until the rotation closes, and calling the match there would hand
	/// it to whoever had the most turns rather than the best answers.
	///
	/// Under a fixed judge that moment is every round: nobody else ever judges, so every writer
	/// has written every round and the scores are already comparable.
	/// </summary>
	public static RoundAdvance CompleteRound(
		CategoriesState state,
		IReadOnlyList<string> seatOrder,
		CategoryDeckDef deck,
		CategoriesRulesConfig rules)
	{
		var round = state.Round;
		round.Phase = CategoriesRoundPhase.Finished;
		state.Players.First(player => player.PlayerId == round.JudgeId).RoundsJudged++;

		var contenders = Contenders(state, rules).ToList();
		var rotationComplete = HasFixedJudge(rules)
			|| state.Players.All(player => player.RoundsJudged >= state.Cycle);
		var tieBreaker = false;
		if (rotationComplete)
		{
			var best = contenders.Max(player => player.Score);
			var decided = rules.EndMode == "score"
				? best >= rules.TargetScore
				: state.Cycle >= rules.Cycles;
			if (decided)
			{
				var leaders = contenders.Where(player => player.Score == best).ToList();
				if (leaders.Count == 1)
				{
					return new RoundAdvance(GameOver: true, WinnerId: leaders[0].PlayerId, TieBreakerStarted: false);
				}
				tieBreaker = true;
			}
			state.Cycle++;
		}

		// A fixed judge keeps the seat as long as they are still at the table; if they left, the
		// next seat inherits it rather than leaving the match with nobody to rule.
		var judgeId = HasFixedJudge(rules)
			&& state.Players.Any(player => player.PlayerId == round.JudgeId)
				? round.JudgeId
				: NextJudge(state, seatOrder, round.JudgeId);
		state.Round = DealRound(state, deck, rules, round.RoundNumber + 1, judgeId);
		return new RoundAdvance(false, null, tieBreaker);
	}
}
