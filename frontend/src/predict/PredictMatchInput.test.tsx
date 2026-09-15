import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../lib/apiClient';
import type { PredictMatch } from '../types/predict';
import { PredictMatchInput } from './PredictMatchInput';

// REQ-1302/1303/1306: PredictMatchInput.tsx's own direct, isolated suite —
// PredictScreen.test.tsx only exercises this component indirectly (through
// a full round fetch/render); this file is additive coverage of the same
// submission/validation/lock-detection logic in isolation, not a
// replacement for that file. Same `vi.mock` module-mock convention as
// frontend/src/auth/DeleteAccountScreen.test.tsx uses for `../lib/turnstile`
// — a shared top-level mock fn wired into the module mock's factory, reset
// in `afterEach` rather than re-declared per test.
const submitPredictionMock = vi.fn();
vi.mock('../lib/predict', () => ({
  submitPrediction: (...args: unknown[]) => submitPredictionMock(...args),
}));

function buildMatch(overrides: Partial<PredictMatch> = {}): PredictMatch {
  return {
    matchId: 'm1',
    homeTeamName: 'Arsenal',
    awayTeamName: 'Chelsea',
    kickoffUtc: '2026-09-13T14:00:00Z',
    homeGoals: null,
    awayGoals: null,
    ...overrides,
  };
}

describe('PredictMatchInput', () => {
  afterEach(() => {
    submitPredictionMock.mockReset();
  });

  it('REQ1302_FieldChange_UpdatesDisplayedValue', async () => {
    const user = userEvent.setup();
    render(
      <PredictMatchInput
        match={buildMatch()}
        accessToken="token"
        disabled={false}
        onSaved={vi.fn()}
        onLockDetected={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');

    expect(screen.getByLabelText('Arsenal predicted goals')).toHaveValue(2);
    expect(screen.getByLabelText('Chelsea predicted goals')).toHaveValue(1);
  });

  it('REQ1302_FieldChange_ClearsPriorErrorStatusOnEdit', async () => {
    const user = userEvent.setup();
    render(
      <PredictMatchInput
        match={buildMatch()}
        accessToken="token"
        disabled={false}
        onSaved={vi.fn()}
        onLockDetected={vi.fn()}
      />,
    );

    // Leave the away field blank so the client-side validator rejects the
    // save without ever calling submitPrediction — the cheapest way to land
    // on `status: 'error'` before asserting the edit-clears-it behavior.
    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(
      await screen.findByText('Enter a whole number, 0 or higher, for both scores.'),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');

    expect(
      screen.queryByText('Enter a whole number, 0 or higher, for both scores.'),
    ).not.toBeInTheDocument();
  });

  it('REQ1302_FieldChange_ClearsPriorSavedStatusOnEdit', async () => {
    const user = userEvent.setup();
    submitPredictionMock.mockResolvedValueOnce({ matchId: 'm1', homeGoals: 2, awayGoals: 1 });
    render(
      <PredictMatchInput
        match={buildMatch()}
        accessToken="token"
        disabled={false}
        onSaved={vi.fn()}
        onLockDetected={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');
    await user.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Saved.')).toBeInTheDocument();

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '3');

    expect(screen.queryByText('Saved.')).not.toBeInTheDocument();
  });

  it('REQ1302_Save_CallsSubmitPredictionAndOnSavedWithServerReturnedValues', async () => {
    const user = userEvent.setup();
    // Deliberately different from what's typed, to prove onSaved reflects
    // the server's response rather than echoing the raw typed input.
    submitPredictionMock.mockResolvedValueOnce({ matchId: 'm1', homeGoals: 9, awayGoals: 8 });
    const onSaved = vi.fn();
    render(
      <PredictMatchInput
        match={buildMatch()}
        accessToken="token-abc"
        disabled={false}
        onSaved={onSaved}
        onLockDetected={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(submitPredictionMock).toHaveBeenCalledWith('token-abc', 'm1', 2, 1);
    expect(onSaved).toHaveBeenCalledWith('m1', 9, 8);
  });

  it('REQ1302_Save_ReachableViaKeyboardAndFiresOnSaved', async () => {
    const user = userEvent.setup();
    submitPredictionMock.mockResolvedValueOnce({ matchId: 'm1', homeGoals: 4, awayGoals: 4 });
    const onSaved = vi.fn();
    render(
      <PredictMatchInput
        match={buildMatch()}
        accessToken="token"
        disabled={false}
        onSaved={onSaved}
        onLockDetected={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');

    // Tab from the away field lands on the Save button next (it's the only
    // remaining tabbable control in this row) — the closest real analog in
    // this component to "keyboard selection" of an option.
    await user.tab();
    expect(screen.getByRole('button', { name: 'Save' })).toHaveFocus();
    await user.keyboard('{Enter}');

    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(submitPredictionMock).toHaveBeenCalledWith('token', 'm1', 2, 1);
    expect(onSaved).toHaveBeenCalledWith('m1', 4, 4);
  });

  it.each([
    ['blank', '', '1'],
    // A `type="number"` input already filters out plain letters at the DOM
    // level (confirmed against this exact input: typing "abc" leaves the
    // value empty, indistinguishable from the blank case above) — a decimal
    // is the real, reachable way a value that fails the component's
    // `^\d+$` integer check ever reaches its change handler.
    ['non-integer', '2.5', '1'],
    ['negative', '-1', '1'],
  ])(
    'REQ1302_Save_RejectsInvalidInput_%s_WithoutCallingSubmitPredictionOrOnSaved',
    async (_label, homeInput, awayInput) => {
      const user = userEvent.setup();
      const onSaved = vi.fn();
      render(
        <PredictMatchInput
          match={buildMatch()}
          accessToken="token"
          disabled={false}
          onSaved={onSaved}
          onLockDetected={vi.fn()}
        />,
      );

      if (homeInput) await user.type(screen.getByLabelText('Arsenal predicted goals'), homeInput);
      await user.type(screen.getByLabelText('Chelsea predicted goals'), awayInput);
      await user.click(screen.getByRole('button', { name: 'Save' }));

      expect(
        await screen.findByText('Enter a whole number, 0 or higher, for both scores.'),
      ).toBeInTheDocument();
      expect(submitPredictionMock).not.toHaveBeenCalled();
      expect(onSaved).not.toHaveBeenCalled();
    },
  );

  it('REQ1303_Save_A409ResponseShowsTheDescribedErrorAndCallsOnLockDetected', async () => {
    const user = userEvent.setup();
    submitPredictionMock.mockRejectedValueOnce(
      new ApiError('Round is locked', 'The round locked before your save was received.', 409),
    );
    const onLockDetected = vi.fn();
    const onSaved = vi.fn();
    render(
      <PredictMatchInput
        match={buildMatch()}
        accessToken="token"
        disabled={false}
        onSaved={onSaved}
        onLockDetected={onLockDetected}
      />,
    );

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(
      await screen.findByText('The round locked before your save was received.'),
    ).toBeInTheDocument();
    expect(onLockDetected).toHaveBeenCalledTimes(1);
    expect(onSaved).not.toHaveBeenCalled();
  });

  it('REQ1303_Save_ANonConflictFailureShowsTheDescribedErrorWithoutCallingOnLockDetected', async () => {
    const user = userEvent.setup();
    submitPredictionMock.mockRejectedValueOnce(
      new ApiError('Invalid prediction', 'Goals must be zero or greater.', 400),
    );
    const onLockDetected = vi.fn();
    render(
      <PredictMatchInput
        match={buildMatch()}
        accessToken="token"
        disabled={false}
        onSaved={vi.fn()}
        onLockDetected={onLockDetected}
      />,
    );

    await user.type(screen.getByLabelText('Arsenal predicted goals'), '2');
    await user.type(screen.getByLabelText('Chelsea predicted goals'), '1');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Goals must be zero or greater.')).toBeInTheDocument();
    expect(onLockDetected).not.toHaveBeenCalled();
  });

  it('REQ1303_1306_Disabled_DisablesBothInputsAndSaveButton', () => {
    render(
      <PredictMatchInput
        match={buildMatch()}
        accessToken="token"
        disabled={true}
        onSaved={vi.fn()}
        onLockDetected={vi.fn()}
      />,
    );

    expect(screen.getByLabelText('Arsenal predicted goals')).toBeDisabled();
    expect(screen.getByLabelText('Chelsea predicted goals')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('REQ1303_1306_Disabled_DiscardsUnsavedEditsBackToTheMatchsStoredValues', async () => {
    const user = userEvent.setup();
    const { rerender } = render(
      <PredictMatchInput
        match={buildMatch({ homeGoals: 1, awayGoals: 0 })}
        accessToken="token"
        disabled={false}
        onSaved={vi.fn()}
        onLockDetected={vi.fn()}
      />,
    );

    // An unsaved edit sitting in the field...
    await user.clear(screen.getByLabelText('Arsenal predicted goals'));
    await user.type(screen.getByLabelText('Arsenal predicted goals'), '5');
    expect(screen.getByLabelText('Arsenal predicted goals')).toHaveValue(5);

    // ...is discarded and re-synced to the match's own stored values once
    // the round/player locks (REQ-1303/1306) and `disabled` flips true.
    rerender(
      <PredictMatchInput
        match={buildMatch({ homeGoals: 1, awayGoals: 0 })}
        accessToken="token"
        disabled={true}
        onSaved={vi.fn()}
        onLockDetected={vi.fn()}
      />,
    );

    expect(screen.getByLabelText('Arsenal predicted goals')).toHaveValue(1);
    expect(screen.getByLabelText('Chelsea predicted goals')).toHaveValue(0);
  });
});
