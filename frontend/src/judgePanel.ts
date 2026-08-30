// judgePanel.ts — naming the judge of a categories table, before a match starts.
//
// Only when the table plays with a FIXED judge. That player referees every round and writes in
// none of them, so who it is shapes the whole match: it belongs at the table, in front of
// everyone, the way the teams are arranged there — not inside the game, where "the host" is not
// even a fact (the turn order is shuffled at start, so the first seat is nobody in particular).
//
// Simpler than teamPanel on purpose: one choice, not an arrangement. A radio group is exactly
// what "one of these people" means, every screen reader already understands it, and it needs no
// roving navigator of its own.

import type { GameInfo, LobbyPlayer } from './models.js';

export interface JudgePanelDeps {
	t: (key: string) => string;
	/** Only the host names the judge; everyone else reads the same panel. */
	isHost: () => boolean;
	/** Name the judge (or clear the choice with null). */
	assign: (playerId: string | null) => Promise<void>;
	announce: (text: string) => void;
	error: (message: string) => void;
}

/** Whether this table is one where a judge has to be named at all. */
export function judgeIsNamedHere(game: GameInfo | null): boolean {
	return game?.gameType === 'categories' && game?.ruleValues?.categoriesJudgeMode === 'fixed';
}

/**
 * Who judges if the match starts as things stand: the host's choice when there is one, and
 * otherwise the first seat, which is what the engine falls back to. The panel shows that fallback
 * as chosen rather than showing nothing, so nobody finds out who is refereeing by starting.
 */
export function effectiveJudgeId(game: GameInfo | null): string | null {
	if (!game) return null;
	const named = game.fixedJudgeId;
	if (named && game.players.some(player => player.id === named)) return named;
	return game.players[0]?.id ?? null;
}

export class JudgePanel {
	private panel: HTMLElement | null = null;
	private deps: JudgePanelDeps | null = null;
	/** The table as it stands. The controls outlive any one repaint now, so a handler must read
	 *  the CURRENT table rather than the one it was built with. */
	private table: GameInfo | null = null;

	init(panel: HTMLElement | null, deps: JudgePanelDeps): void {
		this.panel = panel;
		this.deps = deps;
	}

	/**
	 * Repaint from authoritative state. Hides itself entirely when no judge is named here.
	 *
	 * Choosing a judge is itself an authoritative change, so this runs on the very repaint that
	 * answers it — and moving through a radio group with the arrows chooses on every key. Building
	 * the controls again there would destroy the radio the host is standing on and replace it with
	 * an identical one: focus falls to the document in between, and a screen reader re-reads the
	 * whole context from the window title down, on every single arrow press. So the controls are
	 * built ONCE per roster and only their state is synchronised afterwards.
	 */
	render(game: GameInfo | null): void {
		const panel = this.panel;
		if (!panel || !this.deps) return;
		this.table = game;
		const named = judgeIsNamedHere(game);
		panel.hidden = !named;
		panel.classList.toggle('hidden', !named);
		if (!game || !named) {
			panel.replaceChildren();
			delete panel.dataset.builtFor;
			return;
		}

		// What the CONTROLS depend on: who is at the table, and whether they are mine to move.
		// Not who was chosen — that is state, and state is synchronised in place.
		const builtFor = `${this.deps.isHost()}:${game.players.map(player => player.id).join(',')}`;
		if (panel.dataset.builtFor !== builtFor) {
			panel.dataset.builtFor = builtFor;
			this.build(panel, game);
		}
		this.syncChoice(panel, game);
	}

	/** Mark whoever judges as things stand, without touching the DOM the keyboard is inside. */
	private syncChoice(panel: HTMLElement, game: GameInfo): void {
		const chosen = effectiveJudgeId(game);
		panel.querySelectorAll<HTMLInputElement>('input[data-judge-id]').forEach(input => {
			const isChosen = input.dataset.judgeId === chosen;
			// Only when it actually differs: assigning `checked` the value it already has is
			// harmless, but assigning it at all on a focused radio is not worth risking.
			if (input.checked !== isChosen) input.checked = isChosen;
		});
	}

	/**
	 * The room hears who was named — everybody except the person who named them. The host moved a
	 * radio, and a radio says its own name and its own state as it is chosen: repeating it through
	 * the live region is the same fact twice, one immediately after the other. Everyone else
	 * changed nothing and would otherwise learn it only by going back to read the panel.
	 */
	announceAssigned(data: { playerName?: string | null }): void {
		if (!this.deps || this.deps.isHost()) return;
		this.deps.announce(data.playerName
			? this.deps.t('lobby.judgeNamed').replace('{{player}}', data.playerName)
			: this.deps.t('lobby.judgeCleared'));
	}

	private build(panel: HTMLElement, game: GameInfo): void {
		const t = this.deps!.t;
		const fieldset = document.createElement('fieldset');
		fieldset.className = 'judge-panel__group';
		const legend = document.createElement('legend');
		legend.textContent = t('lobby.judgeTitle');
		fieldset.appendChild(legend);

		const hint = document.createElement('p');
		hint.className = 'judge-panel__hint';
		hint.id = 'judge-panel-hint';
		hint.textContent = t(this.deps!.isHost() ? 'lobby.judgeHintHost' : 'lobby.judgeHintGuest');
		fieldset.appendChild(hint);

		const list = document.createElement('div');
		list.className = 'judge-panel__list';
		for (const player of game.players) {
			list.appendChild(this.option(player));
		}
		fieldset.appendChild(list);
		panel.replaceChildren(fieldset);
	}

	private option(player: LobbyPlayer): HTMLElement {
		const row = document.createElement('div');
		row.className = 'judge-panel__option';
		const input = document.createElement('input');
		input.type = 'radio';
		input.name = 'table-judge';
		input.id = `table-judge-${player.id}`;
		input.dataset.judgeId = player.id;
		input.setAttribute('aria-describedby', 'judge-panel-hint');
		// Never the disabled attribute: a guest still reads the choice with the keyboard, and a
		// control nobody can reach is a control nobody can be told about either.
		if (!this.deps!.isHost()) {
			input.setAttribute('aria-disabled', 'true');
			input.addEventListener('click', event => event.preventDefault());
			input.addEventListener('keydown', event => {
				if (event.key === ' ' || event.key === 'Enter') event.preventDefault();
			});
		} else {
			input.addEventListener('change', () => { void this.choose(player.id); });
		}
		const label = document.createElement('label');
		label.htmlFor = input.id;
		label.textContent = player.name;
		row.append(input, label);
		return row;
	}

	private async choose(playerId: string): Promise<void> {
		if (playerId === this.table?.fixedJudgeId) return;
		try {
			await this.deps!.assign(playerId);
		} catch {
			this.deps!.error(this.deps!.t('lobby.errors.assignJudge'));
		}
	}
}

export const judgePanel = new JudgePanel();
