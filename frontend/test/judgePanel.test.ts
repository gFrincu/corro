import test from 'node:test';
import assert from 'node:assert/strict';
import { JudgePanel, judgeIsNamedHere, effectiveJudgeId } from '../src/judgePanel.js';
import type { GameInfo } from '../src/models.js';
import { installFakeI18next, setupDom } from './helpers/dom.js';

setupDom();
installFakeI18next('es');

const translate = (key: string) => (globalThis as any).window.i18next.t(key);

function table(overrides: Partial<GameInfo> = {}): GameInfo {
	return {
		gameId: 'g1',
		hostId: 'p0',
		inviteCode: 'ABC123',
		status: 'waiting_for_players',
		maxPlayers: 3,
		gameType: 'categories',
		ruleValues: { categoriesJudgeMode: 'fixed' },
		players: [
			{ id: 'p0', name: 'Ada', token: 'quill', isHost: true, isReady: true, joinedAt: '' },
			{ id: 'p1', name: 'Berto', token: 'clock', isHost: false, isReady: true, joinedAt: '' },
			{ id: 'p2', name: 'Cora', token: 'bell', isHost: false, isReady: true, joinedAt: '' },
		],
		...overrides,
	} as GameInfo;
}

function harness(isHost: boolean) {
	document.body.innerHTML = '<div id="panel"></div>';
	const element = document.getElementById('panel')!;
	const assigned: (string | null)[] = [];
	const announced: string[] = [];
	const panel = new JudgePanel();
	panel.init(element, {
		t: translate,
		isHost: () => isHost,
		assign: async playerId => { assigned.push(playerId); },
		announce: text => { announced.push(text); },
		error: text => { announced.push(`error:${text}`); },
	});
	return { panel, element, assigned, announced };
}

const radios = (element: HTMLElement) =>
	[...element.querySelectorAll<HTMLInputElement>('input[type="radio"]')];

test('the panel only exists where one judge referees the whole match', () => {
	assert.equal(judgeIsNamedHere(table()), true);
	assert.equal(judgeIsNamedHere(table({ ruleValues: { categoriesJudgeMode: 'rotating' } })), false);
	assert.equal(judgeIsNamedHere(table({ gameType: 'trivia' })), false);
	assert.equal(judgeIsNamedHere(null), false);

	const h = harness(true);
	h.panel.render(table({ ruleValues: { categoriesJudgeMode: 'rotating' } }));
	assert.equal(h.element.hidden, true);
	assert.equal(h.element.children.length, 0);
});

test('nobody named still shows who WOULD judge, rather than an empty question', () => {
	// The engine falls back to the first seat, so the panel says so instead of letting the table
	// find out by starting the match.
	assert.equal(effectiveJudgeId(table()), 'p0');
	assert.equal(effectiveJudgeId(table({ fixedJudgeId: 'p2' })), 'p2');
	// A seat that left is not a judge: the fallback takes over again.
	assert.equal(effectiveJudgeId(table({ fixedJudgeId: 'gone' })), 'p0');

	const h = harness(true);
	h.panel.render(table());

	assert.equal(radios(h.element)[0].checked, true);
});

test('the host names a judge, and every seat is one named choice', () => {
	const h = harness(true);
	h.panel.render(table({ fixedJudgeId: 'p1' }));

	const options = radios(h.element);
	assert.deepEqual(options.map(input => input.dataset.judgeId), ['p0', 'p1', 'p2']);
	assert.equal(options[1].checked, true);
	// One group with one name, and every option says what choosing it costs.
	assert.equal(h.element.querySelector('legend')!.textContent, translate('lobby.judgeTitle'));
	assert.ok(options.every(input => input.getAttribute('aria-describedby') === 'judge-panel-hint'));
	assert.match(h.element.querySelector('#judge-panel-hint')!.textContent!, /no escribe/i);
	for (const input of options) {
		const label = h.element.querySelector<HTMLLabelElement>(`label[for="${input.id}"]`);
		assert.ok(label && label.textContent, 'every choice is named by a real label');
	}

	options[2].checked = true;
	options[2].dispatchEvent(new window.Event('change', { bubbles: true }));
	assert.deepEqual(h.assigned, ['p2']);
});

test('a guest reads the choice and cannot move it, without the disabled attribute', () => {
	const h = harness(false);
	h.panel.render(table({ fixedJudgeId: 'p1' }));

	const options = radios(h.element);
	assert.equal(options[1].checked, true);
	for (const input of options) {
		// Reachable and readable: disabled would take it out of the keyboard's reach entirely.
		assert.equal(input.hasAttribute('disabled'), false);
		assert.equal(input.getAttribute('aria-disabled'), 'true');
	}
	assert.match(h.element.querySelector('#judge-panel-hint')!.textContent!, /crea/i);

	const event = new window.Event('click', { bubbles: true, cancelable: true });
	options[0].dispatchEvent(event);
	assert.equal(event.defaultPrevented, true);
	assert.deepEqual(h.assigned, []);
});

test('the room hears who was named, and hears when nobody is', () => {
	const guest = harness(false);

	guest.panel.announceAssigned({ playerName: 'Cora' });
	guest.panel.announceAssigned({ playerName: null });

	assert.match(guest.announced[0], /Cora/);
	assert.equal(guest.announced[1], translate('lobby.judgeCleared'));
});

test('the host is not told what the radio they just moved already said', () => {
	// Choosing a radio speaks its name and its state on the spot. Saying it again through the
	// live region is the same fact twice, back to back, for the one person who cannot need it.
	const host = harness(true);

	host.panel.announceAssigned({ playerName: 'Cora' });

	assert.deepEqual(host.announced, []);
});

test('an authoritative repaint gives the keyboard back where it was', () => {
	const h = harness(true);
	h.panel.render(table());
	const berto = radios(h.element)[1];
	berto.focus();

	h.panel.render(table({ fixedJudgeId: 'p2' }));

	assert.equal(
		(document.activeElement as HTMLInputElement).dataset.judgeId,
		'p1',
		'the host stays on the control they were standing on');
});
