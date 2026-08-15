# Blind Classification Worksheet

20 surviving mutants sampled at random (seed `20260815`) from the 94 survivors in the `XGArcade.Games.XGGrid` Stryker.NET run (`StrykerOutput_full/*/reports/mutation-report.json`).

**Selected mutant IDs (ascending):** 13, 20, 31, 32, 60, 61, 263, 277, 362, 388, 413, 422, 430, 431, 451, 457, 458, 460, 464, 466

Evidence only, mechanically extracted — no classification, severity, or hint of a verdict is included anywhere below. Fill in the four fields at the end of each section yourself.

---

## Mutant 13

**File:** `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs`  
**Mutated line(s):** 71-71  
**Mutator:** Equality mutation

**Original:**
```csharp
        if (result.IsCorrect || (result.DisambiguationCandidates?.Count ?? 0) > 0)
```
**Mutated replacement:**
```csharp
(result.DisambiguationCandidates?.Count ?? 0) < 0
```

### Containing method: `ScoreSubmissionAsync` (lines 49-104, 56 lines)

**Leading doc comment:**
```
    // S-009: REQ-210's lock/attempt-cap checks and REQ-202's guess-change
    // policy already happened in Core.Scoring before this was ever called
    // (GuessSubmissionService) — everything here is REQ-207/208/209/211's
    // name-resolution work, delegated to IGridNameMatcher/
    // IGridLiveLookupDispatcher below. This method itself stays on the
    // adapter (rather than moving into either of those classes) because it
    // owns the *orchestration* between them — the instance/cell lookup, and
    // the gate/retry sequencing — not any name-matching or live-lookup logic
    // of its own.
```

```csharp
   49|     public async Task<ScoreResult> ScoreSubmissionAsync(
   50|         Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
   51|     {
   52|         var guessSubmission = (GuessSubmission)submission;
   53| 
   54|         var instance = await gridInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
   55|             ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");
   56| 
   57|         var cell = instance.Cells.FirstOrDefault(c => c.Id == guessSubmission.CellId)
   58|             ?? throw new GuessScoringException($"Cell '{guessSubmission.CellId}' not found in grid instance '{instanceId}'.");
   59| 
   60|         // REQ-208: normalize once — FindMatchAsync below applies the
   61|         // normalized/alias/fuzzy comparisons in order (exact primary name,
   62|         // then alias, then bounded fuzzy).
   63|         var normalized = PlayerNameNormalizer.Normalize(guessSubmission.SubmittedName);
   64| 
   65|         var result = await nameMatcher.FindMatchAsync(cell, normalized, guessSubmission.ChosenPlayerId, instanceId, cancellationToken);
   66| 
   67|         // REQ-209: a genuinely correct guess never needs a live-lookup
   68|         // retry; neither does an ambiguous one — the cell already resolved
   69|         // from cache (just to more than one fitting candidate), which is a
   70|         // different case from "didn't already resolve from cache" below.
   71|         if (result.IsCorrect || (result.DisambiguationCandidates?.Count ?? 0) > 0)
   72|             return result;
   73| 
   74|         // REQ-211 (2026-07-27 fix): grid generation's cached match count
   75|         // (REQ-101/MinValidAnswers) only ever needed to prove this cell had
   76|         // *some* valid answers, never to catalog every one, so a guess can
   77|         // be genuinely correct even though nothing cached confirms it yet —
   78|         // either because this exact player was never synced at all, or
   79|         // because they already exist with one category's attribute cached
   80|         // (from an unrelated cell) but not this cell's other one. Re-running
   81|         // this cell's own country x club intersection query is an upsert,
   82|         // not a fresh insert (PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync,
   83|         // via WikidataLookupService.PersistMatchesAsync), so one call fixes
   84|         // both cases and completes the cell's whole answer key for later
   85|         // guesses too, not just this one name.
   86|         //
   87|         // Gated on PlayerNameIndex first (REQ-207/S-032 built this, 2026-07-17
   88|         // — the "Tier 1, not built" gap this comment used to describe is
   89|         // closed): only a guess that matched a real PlayerNameIndex candidate
   90|         // is worth a live Wikidata round-trip — a name that matched nothing
   91|         // there at all can never be a real player, so paying for a live
   92|         // lookup (and the retry latency that comes with it, this bug
   93|         // bundle's original report) on every wrong guess was pure waste.
   94|         // Every other trigger condition is unchanged: bounded by REQ-210's
   95|         // 2-attempt cap, same as every other guess-time cost, and still a
   96|         // single retry, never a loop.
   97|         if (!await playerNameIndexRepository.ExistsByNormalizedNameAsync(normalized, cancellationToken))
   98|             return result;
   99| 
  100|         if (!await liveLookupDispatcher.TryRefreshCellAsync(cell, cancellationToken))
  101|             return result;
  102| 
  103|         return await nameMatcher.FindMatchAsync(cell, normalized, guessSubmission.ChosenPlayerId, instanceId, cancellationToken);
  104|     }
```

### Data flow

**`result`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs`:
```
   65|         var result = await nameMatcher.FindMatchAsync(cell, normalized, guessSubmission.ChosenPlayerId, instanceId, cancellationToken);
   71|         if (result.IsCorrect || (result.DisambiguationCandidates?.Count ?? 0) > 0)  <-- mutation site
   72|             return result;
   98|             return result;
  101|             return result;
```

### Tests

**`ScoreSubmissionAsync_UnknownInstanceId_ThrowsGuessScoringException`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public void ScoreSubmissionAsync_UnknownInstanceId_ThrowsGuessScoringException()
    {
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new GuessSubmission(Guid.NewGuid(), "Anyone")));
    }
```

**`ScoreSubmissionAsync_UnknownCellId_ThrowsGuessScoringException`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task ScoreSubmissionAsync_UnknownCellId_ThrowsGuessScoringException()
    {
        var (instanceId, _) = await SeedGridInstanceAsync("France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(Guid.NewGuid(), "Anyone")));
    }
```

**`REQ211_ScoreSubmissionAsync_GuessNotInPlayerNameIndex_NeverTriggersLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_GuessNotInPlayerNameIndex_NeverTriggersLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        // Configured on the fake but must never be reached — proves the gate
        // itself blocks the call, not merely that Wikidata found nothing.
        _wikidataLookupService.SetMatches(
            "France", "Arsenal", [new Player { Id = Guid.NewGuid(), FullName = "Should Never Be Reached", WikidataQid = "Qunreached" }]);
        // Deliberately no SeedNameIndexEntry call.
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nobody Real"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a guess that matched nothing in PlayerNameIndex must never trigger a live Wikidata lookup at all");
    }
```

**`REQ211_ScoreSubmissionAsync_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedCountry("Argentina");
        SeedClub("Barcelona");
        var (instanceId, cellId) = await SeedGridInstanceAsync("Argentina", "Barcelona");
        // Some other player already satisfies this cell in the cache — this
        // is what let grid generation accept the pairing in the first place
        // (REQ-101) — but the guessed player himself was never synced, so
        // nothing cached confirms or denies him.
        await SeedPlayerAsync("Javier Mascherano", "Argentina", "Barcelona");
        var messi = new Player { Id = Guid.NewGuid(), FullName = "Lionel Messi", WikidataQid = "Qmessi" };
        _wikidataLookupService.SetMatches("Argentina", "Barcelona", [messi]);
        SeedNameIndexEntry("Lionel Messi");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Lionel Messi"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(messi.Id));
    }
```

**`REQ211_ScoreSubmissionAsync_LiveLookupFallback_NeverTriggeredWhenCachedDataAlreadyAnswersTheGuess`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_LiveLookupFallback_NeverTriggeredWhenCachedDataAlreadyAnswersTheGuess()
    {
        // The fallback must be narrow (ADR-0010) — a guess that already
        // resolves from cached data must never trigger a live call at all.
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var player = await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        // Configured but must never be consulted, since the cache already
        // answers this guess correctly.
        _wikidataLookupService.SetMatches("France", "Arsenal", [new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Qother" }]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Thierry Henry"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }
```

**`REQ211_ScoreSubmissionAsync_GenuinelyIncorrectGuess_LiveLookupFindsNoMatch_StaysIncorrect`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_GenuinelyIncorrectGuess_LiveLookupFindsNoMatch_StaysIncorrect()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        // No matches configured on the fake at all — mirrors a genuine
        // Wikidata no-match, not merely an untried combination.
        SeedNameIndexEntry("Nicolas Anelka");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nicolas Anelka"));

        Assert.That(result.IsCorrect, Is.False);
    }
```

**`REQ211_ScoreSubmissionAsync_GenuinelyIncorrectGuess_LiveLookupFindsNoMatch_OnlyCallsLiveLookupOnce`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_GenuinelyIncorrectGuess_LiveLookupFindsNoMatch_OnlyCallsLiveLookupOnce()
    {
        // ADR-0018: the fallback is a single re-run, never a loop/recursion —
        // bounded by REQ-210's 2-attempts-per-cell cap, same as every other
        // guess-time cost. Even when the re-run still can't answer the
        // guess, LookupAndPersistAsync must be invoked exactly once for this
        // cell's country/club pair, not retried further within the same call.
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        SeedNameIndexEntry("Nicolas Anelka");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nicolas Anelka"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetLastOrigin("France", "Arsenal"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.Default));
    }
```

**`REQ211_ScoreSubmissionAsync_PlayerAlreadyCachedFromUnrelatedCell_LiveLookupFillsOnlyMissingCategory`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_PlayerAlreadyCachedFromUnrelatedCell_LiveLookupFillsOnlyMissingCategory()
    {
        // The bug report's exact repro shape (ADR-0018): the guessed player
        // is not new to the store — they already have this cell's ROW
        // category (nationality) cached from an entirely unrelated
        // country/club pairing (e.g. a different club cell for the same
        // country) — but nothing yet confirms this cell's COLUMN category
        // (club). This must be distinguished from "player doesn't exist at
        // all yet": the live lookup's upsert (by WikidataQid) must find the
        // existing player row and add only the missing club attribute,
        // never create a duplicate Player.
        SeedCountry("Argentina");
        SeedClub("Barcelona");
        var (instanceId, cellId) = await SeedGridInstanceAsync("Argentina", "Barcelona");
        var messi = new Player { Id = Guid.NewGuid(), FullName = "Lionel Messi", WikidataQid = "Qmessi" };
        _dbContext.Players.Add(messi);
        // Cached from some other cell (e.g. Argentina x PSG) — confirms the
        // row category alone, nothing about this cell's club.
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = messi.Id, AttributeType = "nationality", AttributeValue = "Argentina" });
        await _dbContext.SaveChangesAsync();
        _wikidataLookupService.SetMatches("Argentina", "Barcelona", [messi]);
        SeedNameIndexEntry("Lionel Messi");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Lionel Messi"));

        Assert.That(result.IsCorrect, Is.True,
            "a live lookup must resolve a player who already exists with one category cached from an unrelated cell, " +
            "not just a player who is entirely new to the store");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(messi.Id));
        Assert.That(await _dbContext.Players.CountAsync(p => p.WikidataQid == "Qmessi"), Is.EqualTo(1),
            "the live lookup upserts by WikidataQid — it must never create a duplicate Player row for a player already known");
        Assert.That(await _playerOverrideRepository.HasEffectiveAttributeAsync(messi.Id, "club", "Barcelona"), Is.True);
    }
```

**`REQ211_ScoreSubmissionAsync_ClubClubCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_ClubClubCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        // S-030: the fallback's Club x Club branch — same reproduction shape
        // as the Country x Club test above, but for a cell whose row AND
        // column are both category type "club".
        SeedClub("Barcelona");
        SeedClub("Paris Saint-Germain");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "Barcelona", "Paris Saint-Germain",
            rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Club);
        // Some other player already satisfies this Club x Club cell in the
        // cache — this is what let grid generation accept the pairing in
        // the first place (REQ-101) — but the guessed player himself was
        // never synced, so nothing cached confirms or denies him.
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "Some Other Player", WikidataQid = "Qother-clubclub" };
        _dbContext.Players.Add(otherPlayer);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = otherPlayer.Id, AttributeType = "club", AttributeValue = "Barcelona" });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = otherPlayer.Id, AttributeType = "club", AttributeValue = "Paris Saint-Germain" });
        await _dbContext.SaveChangesAsync();
        var neymar = new Player { Id = Guid.NewGuid(), FullName = "Neymar Jr", WikidataQid = "Qneymar" };
        _wikidataLookupService.SetClubClubMatches("Barcelona", "Paris Saint-Germain", [neymar]);
        SeedNameIndexEntry("Neymar Jr");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Neymar Jr"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Club x Club lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(neymar.Id));
        Assert.That(_wikidataLookupService.GetClubClubCallCount("Barcelona", "Paris Saint-Germain"), Is.EqualTo(1));
        Assert.That(
            _wikidataLookupService.GetClubClubLastOrigin("Barcelona", "Paris Saint-Germain"),
            Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
    }
```

**`REQ211_ScoreSubmissionAsync_TrophyCountryCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_TrophyCountryCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedCountry("France");
        SeedTrophy("Ballon d'Or");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var zidane = new Player { Id = Guid.NewGuid(), FullName = "Zinedine Zidane", WikidataQid = "Qzidane" };
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", [zidane]);
        SeedNameIndexEntry("Zinedine Zidane");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Zinedine Zidane"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Trophy x Country lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(zidane.Id));
        Assert.That(_wikidataLookupService.GetTrophyCountryCallCount("Ballon d'Or", "France"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetTrophyCountryLastOrigin("Ballon d'Or", "France"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
    }
```

**`REQ211_ScoreSubmissionAsync_TrophyClubCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ211_ScoreSubmissionAsync_TrophyClubCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedClub("Real Madrid");
        SeedTrophy("Ballon d'Or");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "Real Madrid", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Trophy);
        var modric = new Player { Id = Guid.NewGuid(), FullName = "Luka Modric", WikidataQid = "Qmodric" };
        _wikidataLookupService.SetTrophyClubMatches("Ballon d'Or", "Real Madrid", [modric]);
        SeedNameIndexEntry("Luka Modric");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Luka Modric"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Trophy x Club lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(modric.Id));
        Assert.That(_wikidataLookupService.GetTrophyClubCallCount("Ballon d'Or", "Real Madrid"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetTrophyClubLastOrigin("Ballon d'Or", "Real Madrid"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
    }
```

**`REQ108_ScoreSubmissionAsync_NationalTeamCountryTrophyCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupWithUsesCountryForSportPropertyTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ108_ScoreSubmissionAsync_NationalTeamCountryTrophyCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupWithUsesCountryForSportPropertyTrue()
    {
        // REQ-211's guess-time fallback dispatching through the right query
        // path for a national-team x trophy cell — mirrors
        // REQ114_ScoreSubmissionAsync_NationalTeamCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess
        // below, but the column category is Trophy.
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedTrophy("Ballon d'Or");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "England", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var kane = new Player { Id = Guid.NewGuid(), FullName = "Harry Kane", WikidataQid = "Qkane" };
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "England", [kane]);
        SeedNameIndexEntry("Harry Kane");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Harry Kane"));

        Assert.That(result.IsCorrect, Is.True,
            "a live lookup for a national-team x trophy cell must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(kane.Id));
        Assert.That(_wikidataLookupService.GetTrophyCountryLastUsesCountryForSportProperty("Ballon d'Or", "England"), Is.True,
            "the guess-time fallback (IGridLiveLookupDispatcher.TryRefreshCellAsync -> ResolveCandidateAsync) must re-resolve the full " +
            "CountryDefinition row, including its UsesCountryForSportProperty flag, not just Name/WikidataQid");
    }
```

**`REQ114_ScoreSubmissionAsync_NationalTeamCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ114_ScoreSubmissionAsync_NationalTeamCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        // REQ-211's guess-time fallback dispatching through the right query
        // path for a national-team cell — mirrors
        // REQ211_ScoreSubmissionAsync_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess
        // above, but the row category is a flagged national team.
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        var (instanceId, cellId) = await SeedGridInstanceAsync("England", "Tottenham Hotspur");
        // Some other player already satisfies this cell in the cache (what
        // let grid generation accept the pairing in the first place) — but
        // the guessed player himself was never synced.
        await SeedPlayerAsync("Some Other Spur", "England", "Tottenham Hotspur");
        var kane = new Player { Id = Guid.NewGuid(), FullName = "Harry Kane", WikidataQid = "Qkane" };
        _wikidataLookupService.SetMatches("England", "Tottenham Hotspur", [kane]);
        SeedNameIndexEntry("Harry Kane");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Harry Kane"));

        Assert.That(result.IsCorrect, Is.True,
            "a live lookup for a national-team cell must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(kane.Id));
        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("England", "Tottenham Hotspur"), Is.True,
            "the guess-time fallback (IGridLiveLookupDispatcher.TryRefreshCellAsync -> ResolveCandidateAsync) must re-resolve the full " +
            "CountryDefinition row, including its UsesCountryForSportProperty flag, not just Name/WikidataQid");
    }
```

### REQ/ADR references and comments within the method

References found: REQ-101, REQ-207, REQ-208, REQ-209, REQ-210, REQ-211

Inline comments within the method:
```
// REQ-208: normalize once — FindMatchAsync below applies the
// normalized/alias/fuzzy comparisons in order (exact primary name,
// then alias, then bounded fuzzy).
// REQ-209: a genuinely correct guess never needs a live-lookup
// retry; neither does an ambiguous one — the cell already resolved
// from cache (just to more than one fitting candidate), which is a
// different case from "didn't already resolve from cache" below.
// REQ-211 (2026-07-27 fix): grid generation's cached match count
// (REQ-101/MinValidAnswers) only ever needed to prove this cell had
// *some* valid answers, never to catalog every one, so a guess can
// be genuinely correct even though nothing cached confirms it yet —
// either because this exact player was never synced at all, or
// because they already exist with one category's attribute cached
// (from an unrelated cell) but not this cell's other one. Re-running
// this cell's own country x club intersection query is an upsert,
// not a fresh insert (PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync,
// via WikidataLookupService.PersistMatchesAsync), so one call fixes
// both cases and completes the cell's whole answer key for later
// guesses too, not just this one name.
//
// Gated on PlayerNameIndex first (REQ-207/S-032 built this, 2026-07-17
// — the "Tier 1, not built" gap this comment used to describe is
// closed): only a guess that matched a real PlayerNameIndex candidate
// is worth a live Wikidata round-trip — a name that matched nothing
// there at all can never be a real player, so paying for a live
// lookup (and the retry latency that comes with it, this bug
// bundle's original report) on every wrong guess was pure waste.
// Every other trigger condition is unchanged: bounded by REQ-210's
// 2-attempt cap, same as every other guess-time cost, and still a
// single retry, never a loop.
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 20

**File:** `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs`  
**Mutated line(s):** 113-113  
**Mutator:** String mutation

**Original:**
```csharp
            ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");
```
**Mutated replacement:**
```csharp
$""
```

### Containing method: `GetCellIdsAsync` (lines 110-116, 7 lines)

**Leading doc comment:**
```
    // ADR-0021: round-close's unanswered-cell penalty needs every cell id
    // for the instance, regardless of whether anyone ever guessed it. A
    // trivial IGridInstanceRepository passthrough, not a generation/
    // matching/live-lookup concern — stays on the adapter.
```

```csharp
  110|     public async Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default)
  111|     {
  112|         var instance = await gridInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
  113|             ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");
  114| 
  115|         return instance.Cells.Select(c => c.Id).ToList();
  116|     }
```

### Data flow

**`GuessScoringException`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs`:
```
   55|             ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");
   58|             ?? throw new GuessScoringException($"Cell '{guessSubmission.CellId}' not found in grid instance '{instanceId}'.");
  113|             ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");  <-- mutation site
  143|             ?? throw new GuessScoringException($"Cell '{cellId}' not found.");
```

**`GridInstance`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs`:
```
   55|             ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");
  113|             ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");  <-- mutation site
```

**`instanceId`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs`:
```
   50|         Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
   54|         var instance = await gridInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
   55|             ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");
   58|             ?? throw new GuessScoringException($"Cell '{guessSubmission.CellId}' not found in grid instance '{instanceId}'.");
   65|         var result = await nameMatcher.FindMatchAsync(cell, normalized, guessSubmission.ChosenPlayerId, instanceId, cancellationToken);
  103|         return await nameMatcher.FindMatchAsync(cell, normalized, guessSubmission.ChosenPlayerId, instanceId, cancellationToken);
  110|     public async Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default)
  112|         var instance = await gridInstanceRepository.GetInstanceByIdAsync(instanceId, cancellationToken)
  113|             ?? throw new GuessScoringException($"GridInstance '{instanceId}' not found.");  <-- mutation site
  121|     // fixed allowance — no repository lookup, no branching on instanceId or
  124|     public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
  132|     // instanceId is accepted (matching every other IGameModule method's
  136|     // Deliberately no check that cellId belongs to instanceId either —
  140|     public async Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default)
  152|     // relies on its caller enforcing. instanceId is kept in this method's
  158|         Guid instanceId, string submittedName, CancellationToken cancellationToken = default) =>
```

### Tests

**`REQ206_GetCellIdsAsync_GeneratedInstance_ReturnsEveryCellId`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public async Task REQ206_GetCellIdsAsync_GeneratedInstance_ReturnsEveryCellId()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("France", "Arsenal", 3);
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);
        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);

        var cellIds = await module.GetCellIdsAsync(result.Id);

        Assert.That(cellIds, Is.EquivalentTo(instance!.Cells.Select(c => c.Id)));
    }
```

**`GetCellIdsAsync_UnknownInstanceId_ThrowsGuessScoringException`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGameModuleTests.cs`):
```csharp
[Test]
    public void GetCellIdsAsync_UnknownInstanceId_ThrowsGuessScoringException()
    {
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.GetCellIdsAsync(Guid.NewGuid()));
    }
```

### REQ/ADR references and comments within the method

No REQ-xxx/ADR-xxx references found within the method body or its doc comment.

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 31

**File:** `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`  
**Mutated line(s):** 84-86  
**Mutator:** Conditional (true) mutation

**Original:**
```csharp
        var colCandidatePool = rowCategoryType == colCategoryType
            ? colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
            : colPool;
```
**Mutated replacement:**
```csharp
(true?colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
:colPool)
```

### Containing method: `GenerateInstanceAsync` (lines 54-104, 51 lines)

```csharp
   54|     public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
   55|     {
   56|         var template = await gridInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
   57|             ?? throw new GridGenerationException($"GridTemplate '{config.TemplateId}' not found.");
   58| 
   59|         // REQ-109: candidate values only ever come from the reference
   60|         // tables, never derived ad hoc from PlayerAttribute.
   61|         var countries = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
   62|             .Select(c => new CategoryCandidate(c.Name, c.WikidataQid, c.UsesCountryForSportProperty)).ToList();
   63|         var clubs = (await categoryValueRepository.GetClubsAsync(cancellationToken))
   64|             .Select(c => new CategoryCandidate(c.Name, c.WikidataQid)).ToList();
   65|         // ADR-0061: t.IsTeamTrophy threaded through the same way
   66|         // c.UsesCountryForSportProperty is above — see CategoryCandidate's
   67|         // own doc comment.
   68|         var trophies = (await categoryValueRepository.GetTrophiesAsync(cancellationToken))
   69|             .Select(t => new CategoryCandidate(t.Name, t.WikidataQid, IsTeamTrophy: t.IsTeamTrophy)).ToList();
   70| 
   71|         var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);
   72| 
   73|         var rowPool = PoolFor(rowCategoryType, countries, clubs, trophies);
   74|         var colPool = PoolFor(colCategoryType, countries, clubs, trophies);
   75| 
   76|         // REQ-102: N unique row categories. Any candidate is a valid row
   77|         // header on its own — REQ-107's ban only bites once paired with a
   78|         // column, checked inside PickHeadersAsync below.
   79|         var rowHeaders = Shuffle(rowPool).Take(template.Size).ToList();
   80| 
   81|         // REQ-102's "no row category may be identical to a column category"
   82|         // only bites when both axes share a category type (Club x Club) —
   83|         // Country and Club values can never collide by name.
   84|         var colCandidatePool = rowCategoryType == colCategoryType
   85|             ? colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
   86|             : colPool;
   87| 
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
   89| 
   90|         var instanceId = Guid.NewGuid();
   91|         var instance = new GridInstance
   92|         {
   93|             Id = instanceId,
   94|             TemplateId = template.Id,
   95|             // GridInstanceId set explicitly rather than left to EF Core's
   96|             // relationship fixup via this navigation — Guid is non-nullable,
   97|             // so an unset value would be Guid.Empty, not an obviously-wrong
   98|             // placeholder EF would know to overwrite.
   99|             Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
  100|         };
  101|         await gridInstanceRepository.AddInstanceAsync(instance, cancellationToken);
  102| 
  103|         return new GameInstance { Id = instance.Id };
  104|     }
```

### Data flow

**`colCandidatePool`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
   84|         var colCandidatePool = rowCategoryType == colCategoryType  <-- mutation site
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
  205|         IReadOnlyList<CategoryCandidate> colCandidatePool,
  218|         var remaining = Shuffle(colCandidatePool);
```

**`rowCategoryType`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
   71|         var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);
   73|         var rowPool = PoolFor(rowCategoryType, countries, clubs, trophies);
   84|         var colCandidatePool = rowCategoryType == colCategoryType  <-- mutation site
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
   99|             Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
  202|         string rowCategoryType,
  215|         if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
  225|             rowHeaders.Count, colCategoryType, rowCategoryType, remaining.Count, options.MaxDuration);
  235|             var matchCounts = await TryComputeMatchCountsAsync(rowCategoryType, rowHeaders, colCategoryType, candidate, cancellationToken);
  281|         string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
  287|             matchCounts[i] = await GetMatchCountAsync(rowCategoryType, rowHeaders[i], colCategoryType, candidate, cancellationToken);
  308|         string rowCategoryType, CategoryCandidate row,
  313|             CategoryPairingRules.MapAttributeType(rowCategoryType), row.Name,
  319|             rowCategoryType, row, colCategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
  325|         string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
  334|                     gridInstanceId, row, rowCategoryType, rowHeaders[row], col, colCategoryType, columns[col].Candidate));
  341|         Guid gridInstanceId, int row, string rowCategoryType, CategoryCandidate rowHeader,
  349|             RowCategoryType = rowCategoryType,
```

**`colCategoryType`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
   71|         var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);
   74|         var colPool = PoolFor(colCategoryType, countries, clubs, trophies);
   84|         var colCandidatePool = rowCategoryType == colCategoryType  <-- mutation site
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
   99|             Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
  204|         string colCategoryType,
  215|         if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
  225|             rowHeaders.Count, colCategoryType, rowCategoryType, remaining.Count, options.MaxDuration);
  235|             var matchCounts = await TryComputeMatchCountsAsync(rowCategoryType, rowHeaders, colCategoryType, candidate, cancellationToken);
  239|                     colCategoryType, candidate.Name);
  244|                 colCategoryType, candidate.Name, accepted.Count + 1, rowHeaders.Count);
  282|         string colCategoryType, CategoryCandidate candidate, CancellationToken cancellationToken)
  287|             matchCounts[i] = await GetMatchCountAsync(rowCategoryType, rowHeaders[i], colCategoryType, candidate, cancellationToken);
  309|         string colCategoryType, CategoryCandidate col,
  314|             CategoryPairingRules.MapAttributeType(colCategoryType), col.Name, cancellationToken);
  319|             rowCategoryType, row, colCategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
  326|         string colCategoryType, IReadOnlyList<(CategoryCandidate Candidate, int[] MatchCounts)> columns)
  334|                     gridInstanceId, row, rowCategoryType, rowHeaders[row], col, colCategoryType, columns[col].Candidate));
  342|         int col, string colCategoryType, CategoryCandidate colHeader) =>
  351|             ColCategoryType = colCategoryType,
```

### Tests

**`REQ101_GridGeneration_DiscardsCellWithFewerThanMinimumAnswers`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_DiscardsCellWithFewerThanMinimumAnswers()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        // Four candidates below MinValidAnswers, plus exactly one that meets
        // it. Whichever order the service's internal shuffle tries them in,
        // only "GoodClub" can ever be accepted — so asserting the final
        // header is "GoodClub" proves the too-few-answers candidates were
        // discarded and retried past, not that they got lucky first.
        SeedClub("WeakClub0");
        SeedClub("WeakClub1");
        SeedClub("WeakClub2");
        SeedClub("WeakClub3");
        SeedClub("GoodClub");
        SeedCachedMatches("France", "WeakClub0", 0);
        SeedCachedMatches("France", "WeakClub1", 1);
        SeedCachedMatches("France", "WeakClub2", 2);
        SeedCachedMatches("France", "WeakClub3", 2);
        SeedCachedMatches("France", "GoodClub", 3);
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].RowCategoryValue, Is.EqualTo("France"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("GoodClub"));
    }
```

**`REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxAttemptsExhausted`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxAttemptsExhausted()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        // Five club candidates, none ever satisfying MinValidAnswers=5 (all
        // cached at 0) — with MaxAttempts=3, the loop must abort before
        // exhausting the candidate pool.
        for (var i = 0; i < 5; i++)
        {
            SeedClub($"NeverEnoughClub{i}");
            SeedCachedMatches("France", $"NeverEnoughClub{i}", 0);
        }
        var service = BuildService(minValidAnswers: 5, maxAttempts: 3);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("3 attempts"));
    }
```

**`REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxDurationExceeded`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxDurationExceeded()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        for (var i = 0; i < 5; i++)
            SeedClub($"SlowClub{i}");
        // No SeedCachedMatches call — every candidate is a genuine cache
        // miss, forcing GetMatchCountAsync down the live-lookup path
        // (FakeWikidataLookupService's onCalled hook below) every time,
        // same as the incident's cold-cache scenario. None of them have any
        // configured match either, so every one is rejected on its own
        // terms too — the point of this test is that the deadline trips
        // first, not that a candidate would eventually have been rejected
        // anyway.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(30), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("00:00:30"), "should name the configured MaxDuration, not a raw attempt count");
    }
```

**`REQ101_GridGeneration_FastSuccessfulRun_WellUnderMaxDuration_SucceedsUnaffected`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_FastSuccessfulRun_WellUnderMaxDuration_SucceedsUnaffected()
    {
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedMatches("France", "Barcelona", 2);
        SeedCachedMatches("Spain", "Arsenal", 2);
        SeedCachedMatches("Spain", "Barcelona", 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20, maxDuration: TimeSpan.FromSeconds(5));

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4),
            "an ordinary all-cache-hit run must succeed normally — MaxDuration must not abort a run that never gets close to it");
    }
```

**`REQ101_GridGeneration_AbortsWithGridGenerationException_WhenClockLandsExactlyOnDeadline`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenClockLandsExactlyOnDeadline()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("ClubA");
        SeedClub("ClubB");
        // Neither club has cached matches or a configured live match — both
        // are genuine cache misses forced through the live-lookup path, and
        // both would be rejected on their own terms too. The point is
        // whether a second attempt is even tried once the clock lands
        // exactly on the deadline after the first.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(20), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("found 0/1 valid headers in 1 attempts"),
            "must abort on the very next check after landing exactly on the deadline, before a second live lookup");
        Assert.That(
            wikidataLookupService.GetCallCount("France", "ClubA") + wikidataLookupService.GetCallCount("France", "ClubB"),
            Is.EqualTo(1), "only the first candidate's live lookup should ever run — the second must never be attempted");
    }
```

**`REQ101_GridGeneration_ClubClubPairing_AbortsWithGridGenerationException_WhenMaxDurationExceeded`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_ClubClubPairing_AbortsWithGridGenerationException_WhenMaxDurationExceeded()
    {
        var template = SeedTemplate(size: 1);
        // Zero countries seeded -> Country x Club is infeasible, forcing
        // Club x Club regardless of the injected Random (same technique the
        // other Club x Club tests in this file use).
        for (var i = 0; i < 4; i++)
            SeedClub($"SlowClub{i}");
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(30), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("00:00:30"));
    }
```

**`REQ101_GridGeneration_CacheMiss_FallsBackToLiveLookupAndSucceeds`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_CacheMiss_FallsBackToLiveLookupAndSucceeds()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        // No cached PlayerAttribute rows for France/Arsenal at all — this is
        // a pure cache miss, so the live lookup is the only source of match
        // data for this candidate.
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakeLivePlayers("Arsenal", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("Arsenal"));
        Assert.That(await _playerAttributeRepository.CountPlayersWithBothAttributesAsync(
            "nationality", "France", "club", "Arsenal"), Is.EqualTo(3),
            "a live lookup persists immediately, same request, same as the real WikidataLookupService (ADR-0010) — " +
            "not left for the cache to somehow already have known about");
        // ADR-0029: a generation-time cache-miss is a routine sync, trusted
        // as ground truth — distinct from REQ-211's guess-time fallback,
        // which stays reviewable (see GridLiveLookupDispatcherTests).
        Assert.That(_wikidataLookupService.GetLastOrigin("France", "Arsenal"), Is.EqualTo(WikidataLookupOrigin.Sync));
        // REQ-110 (2026-07-28 "cache-warming-specific timeout" extension):
        // round generation's own live-lookup call site must keep passing (or
        // omitting, which defaults to) WikidataQueryTimeoutTier.Default —
        // only PlayerCacheWarmingService opts into the wider CacheWarming
        // budget (see PlayerCacheWarmingServiceTests' own coverage of that).
        // A regression guard: this test would fail if GridGenerationService
        // ever started passing CacheWarming here by accident.
        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.Default));
    }
```

**`REQ102_GenerateInstanceAsync_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[TestCase(5)]
    public async Task REQ102_GenerateInstanceAsync_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues(int size)
    {
        var template = SeedTemplate(size);
        var countryNames = Enumerable.Range(0, size).Select(i => $"Country{i}").ToList();
        var clubNames = Enumerable.Range(0, size).Select(i => $"Club{i}").ToList();
        foreach (var countryName in countryNames)
            SeedCountry(countryName);
        foreach (var clubName in clubNames)
            SeedClub(clubName);
        foreach (var countryName in countryNames)
            foreach (var clubName in clubNames)
                SeedCachedMatches(countryName, clubName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 50);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(size * size));

        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(size));
        Assert.That(colValues, Has.Count.EqualTo(size));
        Assert.That(rowValues.Intersect(colValues), Is.Empty, "no row category value may equal a column category value");
    }
```

**`REQ107_GenerateInstanceAsync_NeverProducesCountryCountryPairing`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ107_GenerateInstanceAsync_NeverProducesCountryCountryPairing()
    {
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedMatches("France", "Barcelona", 2);
        SeedCachedMatches("Spain", "Arsenal", 2);
        SeedCachedMatches("Spain", "Barcelona", 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Country));
    }
```

**`REQ107_GenerateInstanceAsync_ClubClubGrid_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ107_GenerateInstanceAsync_ClubClubGrid_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues()
    {
        var template = SeedTemplate(size: 3);
        // Zero countries seeded at all -> Country x Club is infeasible
        // (countryCount=0 < size=3), so SelectPairing deterministically
        // picks Club x Club regardless of the injected Random, once >= 2 *
        // size = 6 distinct clubs exist (REQ-102's no-shared-header rule
        // needs 2x, not just size, distinct clubs for Club x Club).
        var clubNames = Enumerable.Range(0, 6).Select(i => $"Club{i}").ToList();
        foreach (var clubName in clubNames)
            SeedClub(clubName);
        for (var i = 0; i < clubNames.Count; i++)
            for (var j = i + 1; j < clubNames.Count; j++)
                SeedCachedClubClubMatches(clubNames[i], clubNames[j], count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 50);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(9));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Club),
            "SelectPairing must have picked Club x Club, not Country x Club, given zero seeded countries");
        Assert.That(instance.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Country),
            "Country x Country must never be produced (REQ-107), regardless of pairing choice");

        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(3), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(3), "REQ-102: N unique column categories");
        Assert.That(rowValues.Intersect(colValues), Is.Empty,
            "REQ-102: no row category value may equal a column category value — the constraint Club x Club actually needs 2xSize clubs for");
    }
```

**`REQ107_GenerateInstanceAsync_BothPairingsFeasible_CoinFlipsBetweenCountryClubAndClubClub`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ107_GenerateInstanceAsync_BothPairingsFeasible_CoinFlipsBetweenCountryClubAndClubClub()
    {
        // Unlike every other Club x Club test in this file, both pairings
        // are feasible here (1 country, 2 clubs) — SelectPairing's
        // random-coin-flip branch (both feasible) only fires in this shape;
        // every other test either pins FixedChoiceRandom(0)'s default
        // (Country x Club) or starves countries to force Club x Club
        // deterministically regardless of the random draw. This is the only
        // test that actually exercises the "both feasible, _random.Next(2)
        // resolves to Club x Club" branch — without it, a bug that always
        // resolved to Country x Club even when the draw should pick
        // Club x Club (e.g. a swapped ternary) would go uncaught.
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedClubClubMatches("Arsenal", "Barcelona", 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20, random: new FixedChoiceRandom(1));

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Club),
            "with both pairings feasible, FixedChoiceRandom(1) must steer SelectPairing to Club x Club, not the Country x Club default");
    }
```

**`REQ108_GenerateInstanceAsync_TrophyCountryPairing_ProducesGridUsingTrophyCategoryType`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_TrophyCountryPairing_ProducesGridUsingTrophyCategoryType()
    {
        // Zero clubs seeded -> every Club-involving pairing is infeasible.
        // Three trophies (>= size but < 2*size) makes Trophy x Trophy
        // infeasible too, leaving Country x Trophy as the only feasible
        // pairing — deterministic regardless of the injected Random.
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        var trophyNames = Enumerable.Range(0, 3).Select(i => $"Trophy{i}").ToList();
        foreach (var trophyName in trophyNames)
            SeedTrophy(trophyName);
        foreach (var countryName in new[] { "France", "Spain" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyCountryMatches(trophyName, countryName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Trophy),
            "SelectPairing must have picked Country x Trophy — Trophy always second, per the Country/Club-first precedent");
        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(2), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(2), "REQ-102: N unique column categories");
    }
```

**`REQ108_GenerateInstanceAsync_TrophyClubPairing_ProducesGridUsingTrophyCategoryType`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_TrophyClubPairing_ProducesGridUsingTrophyCategoryType()
    {
        // Zero countries seeded -> every Country-involving pairing is
        // infeasible. Three trophies (>= size but < 2*size) makes
        // Trophy x Trophy infeasible too, leaving Club x Trophy as the only
        // feasible pairing — deterministic regardless of the injected Random.
        var template = SeedTemplate(size: 2);
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var trophyNames = Enumerable.Range(0, 3).Select(i => $"Trophy{i}").ToList();
        foreach (var trophyName in trophyNames)
            SeedTrophy(trophyName);
        foreach (var clubName in new[] { "Arsenal", "Barcelona" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyClubMatches(trophyName, clubName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Trophy),
            "SelectPairing must have picked Club x Trophy — Trophy always second, per the Country/Club-first precedent");
        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(2), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(2), "REQ-102: N unique column categories");
    }
```

**`REQ108_SelectPairing_ExactlyOneTrophySeeded_TrophyPairingNeverSelected`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_SelectPairing_ExactlyOneTrophySeeded_TrophyPairingNeverSelected()
    {
        // Pure mechanism coverage (no longer "matching real seed data" —
        // see the ADR-0061 section below for tests against the actual,
        // now-3-trophy production shape): with only one trophy in the pool
        // and size >= 2, trophyCount(1) can never clear `size` for any mixed
        // pairing, nor `size * 2` for Trophy x Trophy — so every Trophy
        // pairing is infeasible and Country x Club is the only choice,
        // regardless of the injected Random.
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedTrophy("Ballon d'Or");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedMatches("France", "Barcelona", 2);
        SeedCachedMatches("Spain", "Arsenal", 2);
        SeedCachedMatches("Spain", "Barcelona", 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Trophy || c.ColCategoryType == CategoryPairingRules.Trophy),
            "with only one trophy in the pool, Trophy can never be selected for any realistic grid size");
    }
```

**`REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_CountryTrophyPairingIsNowSelectable`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_CountryTrophyPairingIsNowSelectable()
    {
        // The real ReferenceDataSeeder shape as of ADR-0061: exactly three
        // trophies, matching names/flags. Zero clubs seeded -> every
        // Club-involving pairing is infeasible; countryCount(3) and
        // trophyCount(3) both clear the default GridSize = 3, so
        // Country x Trophy is the only feasible pairing — deterministic
        // regardless of the injected Random.
        var template = SeedTemplate(size: 3);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedCountry("Brazil");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var trophyNames = new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" };
        foreach (var countryName in new[] { "France", "Spain", "Brazil" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyCountryMatches(trophyName, countryName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Trophy),
            "with the real (now 3-trophy) seeded pool, Country x Trophy must be selectable for a size-3 grid — this reverses S-031's original 'structurally dormant' consequence");
    }
```

**`REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_ClubTrophyPairingIsNowSelectable`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_ClubTrophyPairingIsNowSelectable()
    {
        // Mirror of the Country x Trophy test above — zero countries seeded
        // -> every Country-involving pairing is infeasible, leaving
        // Club x Trophy as the only feasible pairing.
        var template = SeedTemplate(size: 3);
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedClub("Real Madrid");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var trophyNames = new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" };
        foreach (var clubName in new[] { "Arsenal", "Barcelona", "Real Madrid" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyClubMatches(trophyName, clubName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Trophy),
            "with the real (now 3-trophy) seeded pool, Club x Trophy must be selectable for a size-3 grid");
    }
```

**`REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_TrophyTrophyPairingStillInfeasible`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public void REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_TrophyTrophyPairingStillInfeasible()
    {
        // Trophy x Trophy needs trophyCount >= size * 2 = 6 — three trophies
        // still doesn't clear that, even though it now clears the plain
        // `>= size` bar Country x Trophy/Club x Trophy need. Zero countries
        // and zero clubs seeded, so no other pairing is feasible either —
        // GenerateInstanceAsync must abort with GridGenerationException
        // rather than silently produce a Trophy x Trophy grid.
        var template = SeedTemplate(size: 3);
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));
    }
```

**`REQ109_GenerateInstanceAsync_OnlyUsesValuesFromReferenceTables_NeverFromPlayerAttributeAlone`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ109_GenerateInstanceAsync_OnlyUsesValuesFromReferenceTables_NeverFromPlayerAttributeAlone()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("France", "Arsenal", 3);
        // "PhantomClub" has abundant matching data in PlayerAttribute but was
        // never added as a ClubDefinition row — it must never be considered
        // as a candidate, however good its match count.
        SeedCachedMatches("France", "PhantomClub", 10);
        var service = BuildService(minValidAnswers: 1, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells[0].ColCategoryValue, Is.EqualTo("Arsenal"));
        Assert.That(instance.Cells.Select(c => c.ColCategoryValue), Does.Not.Contain("PhantomClub"));
    }
```

**`REQ109_GenerateInstanceAsync_NullWikidataQid_DoesNotThrow_AndDiscardsThroughOrdinaryRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ109_GenerateInstanceAsync_NullWikidataQid_DoesNotThrow_AndDiscardsThroughOrdinaryRetry()
    {
        var template = SeedTemplate(size: 1);
        // No resolved WikidataQid yet (REQ-109) — must not crash generation.
        SeedCountry("Ruritania", wikidataQid: null);
        SeedClub("NoDataClub");   // cache miss; live lookup is skipped (null country QID) -> 0 matches, discarded
        SeedClub("GoodClub");     // cache hit -> accepted without ever needing a live lookup
        SeedCachedMatches("Ruritania", "GoodClub", 2);
        // Configured on the fake, but unreachable via the real contract since
        // the country QID is null — proves the service never gets a match for
        // "NoDataClub" from this path, only from the (absent) cache.
        _wikidataLookupService.SetMatches("Ruritania", "NoDataClub", BuildFakeLivePlayers("NoDataClub", 5));
        var service = BuildService(minValidAnswers: 2, maxAttempts: 5);

        GameInstance? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result!.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells[0].RowCategoryValue, Is.EqualTo("Ruritania"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("GoodClub"));
    }
```

**`REQ114_GenerateInstanceAsync_NationalTeamCountry_PairsWithClubsExactlyLikeAnyOtherCountry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ114_GenerateInstanceAsync_NationalTeamCountry_PairsWithClubsExactlyLikeAnyOtherCountry()
    {
        // No special-casing needed anywhere in grid generation's pairing
        // logic (SelectPairing/CategoryPairingRules) — a flagged country is
        // just another CountryDefinition row.
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        SeedCachedMatches("England", "Tottenham Hotspur", 3);
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].RowCategoryType, Is.EqualTo(CategoryPairingRules.Country));
        Assert.That(instance.Cells[0].RowCategoryValue, Is.EqualTo("England"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("Tottenham Hotspur"));
    }
```

**`REQ114_GenerateInstanceAsync_OrdinaryCountry_StillDispatchesWithFlagFalse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ114_GenerateInstanceAsync_OrdinaryCountry_StillDispatchesWithFlagFalse()
    {
        // The existing P27 path (represented here by
        // UsesCountryForSportProperty = false reaching the lookup service)
        // must stay completely unaffected — this is generation's cache-miss
        // path (GetMatchCountAsync), not the guess-time fallback.
        var template = SeedTemplate(size: 1);
        SeedCountry("France"); // usesCountryForSportProperty defaults to false
        SeedClub("Arsenal");
        // No SeedCachedMatches call — forces the live-lookup path so
        // LookupAndPersistAsync is actually invoked and its flag captured.
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakeLivePlayers("France-Arsenal", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("France", "Arsenal"), Is.False);
    }
```

**`REQ114_GenerateInstanceAsync_NationalTeamCountry_LiveLookupDispatchedWithFlagTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ114_GenerateInstanceAsync_NationalTeamCountry_LiveLookupDispatchedWithFlagTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        // No SeedCachedMatches call — forces the live-lookup path
        // (GetMatchCountAsync's cache miss) so LookupAndPersistAsync is
        // actually invoked and its flag captured.
        _wikidataLookupService.SetMatches("England", "Tottenham Hotspur", BuildFakeLivePlayers("England-Spurs", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("England", "Tottenham Hotspur"), Is.True,
            "CategoryCandidate must carry CountryDefinition.UsesCountryForSportProperty through to the live-lookup dispatch site");
    }
```

**`REQ108_GenerateInstanceAsync_NationalTeamCountryTrophyPairing_LiveLookupDispatchedWithUsesCountryForSportPropertyTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_NationalTeamCountryTrophyPairing_LiveLookupDispatchedWithUsesCountryForSportPropertyTrue()
    {
        // size=1 keeps this deterministic without needing a 3-trophy pool:
        // Country x Club is infeasible (zero clubs seeded), Trophy x Trophy
        // needs trophyCount >= 2, so Country x Trophy is the only feasible
        // pairing with one trophy seeded.
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedTrophy("Ballon d'Or");
        // No SeedCachedTrophyCountryMatches call — forces the live-lookup
        // path so LookupAndPersistTrophyCountryAsync is actually invoked and
        // its flags captured.
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "England", BuildFakeLivePlayers("BallonDor-England", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastUsesCountryForSportProperty("Ballon d'Or", "England"), Is.True,
            "CategoryCandidate must carry CountryDefinition.UsesCountryForSportProperty through to the Trophy x Country live-lookup dispatch site, not silently fall back to P27 (ADR-0035/ADR-0061)");
    }
```

**`REQ108_GenerateInstanceAsync_OrdinaryCountryTrophyPairing_StillDispatchesWithUsesCountryForSportPropertyFalse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_OrdinaryCountryTrophyPairing_StillDispatchesWithUsesCountryForSportPropertyFalse()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France"); // usesCountryForSportProperty defaults to false
        SeedTrophy("Ballon d'Or");
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", BuildFakeLivePlayers("BallonDor-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastUsesCountryForSportProperty("Ballon d'Or", "France"), Is.False);
    }
```

**`REQ108_GenerateInstanceAsync_TeamTrophyCountryPairing_LiveLookupDispatchedWithIsTeamTrophyTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_TeamTrophyCountryPairing_LiveLookupDispatchedWithIsTeamTrophyTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        _wikidataLookupService.SetTrophyCountryMatches("FIFA World Cup", "France", BuildFakeLivePlayers("WorldCup-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastIsTeamTrophy("FIFA World Cup", "France"), Is.True,
            "CategoryCandidate must carry TrophyDefinition.IsTeamTrophy through to the Trophy x Country live-lookup dispatch site (ADR-0061)");
    }
```

**`REQ108_GenerateInstanceAsync_IndividualAwardCountryPairing_StillDispatchesWithIsTeamTrophyFalse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_IndividualAwardCountryPairing_StillDispatchesWithIsTeamTrophyFalse()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", BuildFakeLivePlayers("BallonDor-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastIsTeamTrophy("Ballon d'Or", "France"), Is.False);
    }
```

**`REQ108_GenerateInstanceAsync_TeamTrophyClubPairing_LiveLookupDispatchedWithIsTeamTrophyTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_TeamTrophyClubPairing_LiveLookupDispatchedWithIsTeamTrophyTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedClub("Real Madrid");
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        _wikidataLookupService.SetTrophyClubMatches("UEFA Champions League", "Real Madrid", BuildFakeLivePlayers("ChampionsLeague-RealMadrid", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyClubLastIsTeamTrophy("UEFA Champions League", "Real Madrid"), Is.True,
            "CategoryCandidate must carry TrophyDefinition.IsTeamTrophy through to the Trophy x Club live-lookup dispatch site (ADR-0061)");
    }
```

### REQ/ADR references and comments within the method

References found: ADR-0061, REQ-102, REQ-107, REQ-109

Inline comments within the method:
```
// REQ-109: candidate values only ever come from the reference
// tables, never derived ad hoc from PlayerAttribute.
// ADR-0061: t.IsTeamTrophy threaded through the same way
// c.UsesCountryForSportProperty is above — see CategoryCandidate's
// own doc comment.
// REQ-102: N unique row categories. Any candidate is a valid row
// header on its own — REQ-107's ban only bites once paired with a
// column, checked inside PickHeadersAsync below.
// REQ-102's "no row category may be identical to a column category"
// only bites when both axes share a category type (Club x Club) —
// Country and Club values can never collide by name.
// GridInstanceId set explicitly rather than left to EF Core's
// relationship fixup via this navigation — Guid is non-nullable,
// so an unset value would be Guid.Empty, not an obviously-wrong
// placeholder EF would know to overwrite.
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 32

**File:** `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`  
**Mutated line(s):** 84-86  
**Mutator:** Conditional (false) mutation

**Original:**
```csharp
        var colCandidatePool = rowCategoryType == colCategoryType
            ? colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
            : colPool;
```
**Mutated replacement:**
```csharp
(false?colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
:colPool)
```

### Containing method: `GenerateInstanceAsync` (lines 54-104, 51 lines)

```csharp
   54|     public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
   55|     {
   56|         var template = await gridInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
   57|             ?? throw new GridGenerationException($"GridTemplate '{config.TemplateId}' not found.");
   58| 
   59|         // REQ-109: candidate values only ever come from the reference
   60|         // tables, never derived ad hoc from PlayerAttribute.
   61|         var countries = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
   62|             .Select(c => new CategoryCandidate(c.Name, c.WikidataQid, c.UsesCountryForSportProperty)).ToList();
   63|         var clubs = (await categoryValueRepository.GetClubsAsync(cancellationToken))
   64|             .Select(c => new CategoryCandidate(c.Name, c.WikidataQid)).ToList();
   65|         // ADR-0061: t.IsTeamTrophy threaded through the same way
   66|         // c.UsesCountryForSportProperty is above — see CategoryCandidate's
   67|         // own doc comment.
   68|         var trophies = (await categoryValueRepository.GetTrophiesAsync(cancellationToken))
   69|             .Select(t => new CategoryCandidate(t.Name, t.WikidataQid, IsTeamTrophy: t.IsTeamTrophy)).ToList();
   70| 
   71|         var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);
   72| 
   73|         var rowPool = PoolFor(rowCategoryType, countries, clubs, trophies);
   74|         var colPool = PoolFor(colCategoryType, countries, clubs, trophies);
   75| 
   76|         // REQ-102: N unique row categories. Any candidate is a valid row
   77|         // header on its own — REQ-107's ban only bites once paired with a
   78|         // column, checked inside PickHeadersAsync below.
   79|         var rowHeaders = Shuffle(rowPool).Take(template.Size).ToList();
   80| 
   81|         // REQ-102's "no row category may be identical to a column category"
   82|         // only bites when both axes share a category type (Club x Club) —
   83|         // Country and Club values can never collide by name.
   84|         var colCandidatePool = rowCategoryType == colCategoryType
   85|             ? colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
   86|             : colPool;
   87| 
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
   89| 
   90|         var instanceId = Guid.NewGuid();
   91|         var instance = new GridInstance
   92|         {
   93|             Id = instanceId,
   94|             TemplateId = template.Id,
   95|             // GridInstanceId set explicitly rather than left to EF Core's
   96|             // relationship fixup via this navigation — Guid is non-nullable,
   97|             // so an unset value would be Guid.Empty, not an obviously-wrong
   98|             // placeholder EF would know to overwrite.
   99|             Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
  100|         };
  101|         await gridInstanceRepository.AddInstanceAsync(instance, cancellationToken);
  102| 
  103|         return new GameInstance { Id = instance.Id };
  104|     }
```

### Data flow

**`colCandidatePool`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
   84|         var colCandidatePool = rowCategoryType == colCategoryType  <-- mutation site
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
  205|         IReadOnlyList<CategoryCandidate> colCandidatePool,
  218|         var remaining = Shuffle(colCandidatePool);
```

**`rowCategoryType`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
   71|         var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);
   73|         var rowPool = PoolFor(rowCategoryType, countries, clubs, trophies);
   84|         var colCandidatePool = rowCategoryType == colCategoryType  <-- mutation site
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
   99|             Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
  202|         string rowCategoryType,
  215|         if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
  225|             rowHeaders.Count, colCategoryType, rowCategoryType, remaining.Count, options.MaxDuration);
  235|             var matchCounts = await TryComputeMatchCountsAsync(rowCategoryType, rowHeaders, colCategoryType, candidate, cancellationToken);
  281|         string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
  287|             matchCounts[i] = await GetMatchCountAsync(rowCategoryType, rowHeaders[i], colCategoryType, candidate, cancellationToken);
  308|         string rowCategoryType, CategoryCandidate row,
  313|             CategoryPairingRules.MapAttributeType(rowCategoryType), row.Name,
  319|             rowCategoryType, row, colCategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
  325|         string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
  334|                     gridInstanceId, row, rowCategoryType, rowHeaders[row], col, colCategoryType, columns[col].Candidate));
  341|         Guid gridInstanceId, int row, string rowCategoryType, CategoryCandidate rowHeader,
  349|             RowCategoryType = rowCategoryType,
```

**`colCategoryType`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
   71|         var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);
   74|         var colPool = PoolFor(colCategoryType, countries, clubs, trophies);
   84|         var colCandidatePool = rowCategoryType == colCategoryType  <-- mutation site
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
   99|             Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
  204|         string colCategoryType,
  215|         if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
  225|             rowHeaders.Count, colCategoryType, rowCategoryType, remaining.Count, options.MaxDuration);
  235|             var matchCounts = await TryComputeMatchCountsAsync(rowCategoryType, rowHeaders, colCategoryType, candidate, cancellationToken);
  239|                     colCategoryType, candidate.Name);
  244|                 colCategoryType, candidate.Name, accepted.Count + 1, rowHeaders.Count);
  282|         string colCategoryType, CategoryCandidate candidate, CancellationToken cancellationToken)
  287|             matchCounts[i] = await GetMatchCountAsync(rowCategoryType, rowHeaders[i], colCategoryType, candidate, cancellationToken);
  309|         string colCategoryType, CategoryCandidate col,
  314|             CategoryPairingRules.MapAttributeType(colCategoryType), col.Name, cancellationToken);
  319|             rowCategoryType, row, colCategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
  326|         string colCategoryType, IReadOnlyList<(CategoryCandidate Candidate, int[] MatchCounts)> columns)
  334|                     gridInstanceId, row, rowCategoryType, rowHeaders[row], col, colCategoryType, columns[col].Candidate));
  342|         int col, string colCategoryType, CategoryCandidate colHeader) =>
  351|             ColCategoryType = colCategoryType,
```

### Tests

**`REQ101_GridGeneration_DiscardsCellWithFewerThanMinimumAnswers`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_DiscardsCellWithFewerThanMinimumAnswers()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        // Four candidates below MinValidAnswers, plus exactly one that meets
        // it. Whichever order the service's internal shuffle tries them in,
        // only "GoodClub" can ever be accepted — so asserting the final
        // header is "GoodClub" proves the too-few-answers candidates were
        // discarded and retried past, not that they got lucky first.
        SeedClub("WeakClub0");
        SeedClub("WeakClub1");
        SeedClub("WeakClub2");
        SeedClub("WeakClub3");
        SeedClub("GoodClub");
        SeedCachedMatches("France", "WeakClub0", 0);
        SeedCachedMatches("France", "WeakClub1", 1);
        SeedCachedMatches("France", "WeakClub2", 2);
        SeedCachedMatches("France", "WeakClub3", 2);
        SeedCachedMatches("France", "GoodClub", 3);
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].RowCategoryValue, Is.EqualTo("France"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("GoodClub"));
    }
```

**`REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxAttemptsExhausted`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxAttemptsExhausted()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        // Five club candidates, none ever satisfying MinValidAnswers=5 (all
        // cached at 0) — with MaxAttempts=3, the loop must abort before
        // exhausting the candidate pool.
        for (var i = 0; i < 5; i++)
        {
            SeedClub($"NeverEnoughClub{i}");
            SeedCachedMatches("France", $"NeverEnoughClub{i}", 0);
        }
        var service = BuildService(minValidAnswers: 5, maxAttempts: 3);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("3 attempts"));
    }
```

**`REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxDurationExceeded`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenMaxDurationExceeded()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        for (var i = 0; i < 5; i++)
            SeedClub($"SlowClub{i}");
        // No SeedCachedMatches call — every candidate is a genuine cache
        // miss, forcing GetMatchCountAsync down the live-lookup path
        // (FakeWikidataLookupService's onCalled hook below) every time,
        // same as the incident's cold-cache scenario. None of them have any
        // configured match either, so every one is rejected on its own
        // terms too — the point of this test is that the deadline trips
        // first, not that a candidate would eventually have been rejected
        // anyway.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(30), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("00:00:30"), "should name the configured MaxDuration, not a raw attempt count");
    }
```

**`REQ101_GridGeneration_FastSuccessfulRun_WellUnderMaxDuration_SucceedsUnaffected`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_FastSuccessfulRun_WellUnderMaxDuration_SucceedsUnaffected()
    {
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedMatches("France", "Barcelona", 2);
        SeedCachedMatches("Spain", "Arsenal", 2);
        SeedCachedMatches("Spain", "Barcelona", 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20, maxDuration: TimeSpan.FromSeconds(5));

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4),
            "an ordinary all-cache-hit run must succeed normally — MaxDuration must not abort a run that never gets close to it");
    }
```

**`REQ101_GridGeneration_AbortsWithGridGenerationException_WhenClockLandsExactlyOnDeadline`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_AbortsWithGridGenerationException_WhenClockLandsExactlyOnDeadline()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("ClubA");
        SeedClub("ClubB");
        // Neither club has cached matches or a configured live match — both
        // are genuine cache misses forced through the live-lookup path, and
        // both would be rejected on their own terms too. The point is
        // whether a second attempt is even tried once the clock lands
        // exactly on the deadline after the first.
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(20), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("found 0/1 valid headers in 1 attempts"),
            "must abort on the very next check after landing exactly on the deadline, before a second live lookup");
        Assert.That(
            wikidataLookupService.GetCallCount("France", "ClubA") + wikidataLookupService.GetCallCount("France", "ClubB"),
            Is.EqualTo(1), "only the first candidate's live lookup should ever run — the second must never be attempted");
    }
```

**`REQ101_GridGeneration_ClubClubPairing_AbortsWithGridGenerationException_WhenMaxDurationExceeded`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_ClubClubPairing_AbortsWithGridGenerationException_WhenMaxDurationExceeded()
    {
        var template = SeedTemplate(size: 1);
        // Zero countries seeded -> Country x Club is infeasible, forcing
        // Club x Club regardless of the injected Random (same technique the
        // other Club x Club tests in this file use).
        for (var i = 0; i < 4; i++)
            SeedClub($"SlowClub{i}");
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var wikidataLookupService = new FakeWikidataLookupService(
            onCalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        var service = BuildService(
            minValidAnswers: 5, maxAttempts: 500,
            maxDuration: TimeSpan.FromSeconds(30), timeProvider: clock,
            wikidataLookupService: wikidataLookupService);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        Assert.That(ex!.Message, Does.Contain("exceeding"));
        Assert.That(ex.Message, Does.Contain("00:00:30"));
    }
```

**`REQ101_GridGeneration_CacheMiss_FallsBackToLiveLookupAndSucceeds`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ101_GridGeneration_CacheMiss_FallsBackToLiveLookupAndSucceeds()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        // No cached PlayerAttribute rows for France/Arsenal at all — this is
        // a pure cache miss, so the live lookup is the only source of match
        // data for this candidate.
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakeLivePlayers("Arsenal", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("Arsenal"));
        Assert.That(await _playerAttributeRepository.CountPlayersWithBothAttributesAsync(
            "nationality", "France", "club", "Arsenal"), Is.EqualTo(3),
            "a live lookup persists immediately, same request, same as the real WikidataLookupService (ADR-0010) — " +
            "not left for the cache to somehow already have known about");
        // ADR-0029: a generation-time cache-miss is a routine sync, trusted
        // as ground truth — distinct from REQ-211's guess-time fallback,
        // which stays reviewable (see GridLiveLookupDispatcherTests).
        Assert.That(_wikidataLookupService.GetLastOrigin("France", "Arsenal"), Is.EqualTo(WikidataLookupOrigin.Sync));
        // REQ-110 (2026-07-28 "cache-warming-specific timeout" extension):
        // round generation's own live-lookup call site must keep passing (or
        // omitting, which defaults to) WikidataQueryTimeoutTier.Default —
        // only PlayerCacheWarmingService opts into the wider CacheWarming
        // budget (see PlayerCacheWarmingServiceTests' own coverage of that).
        // A regression guard: this test would fail if GridGenerationService
        // ever started passing CacheWarming here by accident.
        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.Default));
    }
```

**`REQ102_GenerateInstanceAsync_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[TestCase(5)]
    public async Task REQ102_GenerateInstanceAsync_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues(int size)
    {
        var template = SeedTemplate(size);
        var countryNames = Enumerable.Range(0, size).Select(i => $"Country{i}").ToList();
        var clubNames = Enumerable.Range(0, size).Select(i => $"Club{i}").ToList();
        foreach (var countryName in countryNames)
            SeedCountry(countryName);
        foreach (var clubName in clubNames)
            SeedClub(clubName);
        foreach (var countryName in countryNames)
            foreach (var clubName in clubNames)
                SeedCachedMatches(countryName, clubName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 50);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(size * size));

        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(size));
        Assert.That(colValues, Has.Count.EqualTo(size));
        Assert.That(rowValues.Intersect(colValues), Is.Empty, "no row category value may equal a column category value");
    }
```

**`REQ107_GenerateInstanceAsync_NeverProducesCountryCountryPairing`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ107_GenerateInstanceAsync_NeverProducesCountryCountryPairing()
    {
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedMatches("France", "Barcelona", 2);
        SeedCachedMatches("Spain", "Arsenal", 2);
        SeedCachedMatches("Spain", "Barcelona", 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Country));
    }
```

**`REQ107_GenerateInstanceAsync_ClubClubGrid_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ107_GenerateInstanceAsync_ClubClubGrid_ProducesExactlySizeSquaredCellsWithUniqueRowAndColumnValues()
    {
        var template = SeedTemplate(size: 3);
        // Zero countries seeded at all -> Country x Club is infeasible
        // (countryCount=0 < size=3), so SelectPairing deterministically
        // picks Club x Club regardless of the injected Random, once >= 2 *
        // size = 6 distinct clubs exist (REQ-102's no-shared-header rule
        // needs 2x, not just size, distinct clubs for Club x Club).
        var clubNames = Enumerable.Range(0, 6).Select(i => $"Club{i}").ToList();
        foreach (var clubName in clubNames)
            SeedClub(clubName);
        for (var i = 0; i < clubNames.Count; i++)
            for (var j = i + 1; j < clubNames.Count; j++)
                SeedCachedClubClubMatches(clubNames[i], clubNames[j], count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 50);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(9));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Club),
            "SelectPairing must have picked Club x Club, not Country x Club, given zero seeded countries");
        Assert.That(instance.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Country),
            "Country x Country must never be produced (REQ-107), regardless of pairing choice");

        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(3), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(3), "REQ-102: N unique column categories");
        Assert.That(rowValues.Intersect(colValues), Is.Empty,
            "REQ-102: no row category value may equal a column category value — the constraint Club x Club actually needs 2xSize clubs for");
    }
```

**`REQ107_GenerateInstanceAsync_BothPairingsFeasible_CoinFlipsBetweenCountryClubAndClubClub`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ107_GenerateInstanceAsync_BothPairingsFeasible_CoinFlipsBetweenCountryClubAndClubClub()
    {
        // Unlike every other Club x Club test in this file, both pairings
        // are feasible here (1 country, 2 clubs) — SelectPairing's
        // random-coin-flip branch (both feasible) only fires in this shape;
        // every other test either pins FixedChoiceRandom(0)'s default
        // (Country x Club) or starves countries to force Club x Club
        // deterministically regardless of the random draw. This is the only
        // test that actually exercises the "both feasible, _random.Next(2)
        // resolves to Club x Club" branch — without it, a bug that always
        // resolved to Country x Club even when the draw should pick
        // Club x Club (e.g. a swapped ternary) would go uncaught.
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedClubClubMatches("Arsenal", "Barcelona", 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20, random: new FixedChoiceRandom(1));

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Club),
            "with both pairings feasible, FixedChoiceRandom(1) must steer SelectPairing to Club x Club, not the Country x Club default");
    }
```

**`REQ108_GenerateInstanceAsync_TrophyCountryPairing_ProducesGridUsingTrophyCategoryType`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_TrophyCountryPairing_ProducesGridUsingTrophyCategoryType()
    {
        // Zero clubs seeded -> every Club-involving pairing is infeasible.
        // Three trophies (>= size but < 2*size) makes Trophy x Trophy
        // infeasible too, leaving Country x Trophy as the only feasible
        // pairing — deterministic regardless of the injected Random.
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        var trophyNames = Enumerable.Range(0, 3).Select(i => $"Trophy{i}").ToList();
        foreach (var trophyName in trophyNames)
            SeedTrophy(trophyName);
        foreach (var countryName in new[] { "France", "Spain" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyCountryMatches(trophyName, countryName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Trophy),
            "SelectPairing must have picked Country x Trophy — Trophy always second, per the Country/Club-first precedent");
        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(2), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(2), "REQ-102: N unique column categories");
    }
```

**`REQ108_GenerateInstanceAsync_TrophyClubPairing_ProducesGridUsingTrophyCategoryType`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_TrophyClubPairing_ProducesGridUsingTrophyCategoryType()
    {
        // Zero countries seeded -> every Country-involving pairing is
        // infeasible. Three trophies (>= size but < 2*size) makes
        // Trophy x Trophy infeasible too, leaving Club x Trophy as the only
        // feasible pairing — deterministic regardless of the injected Random.
        var template = SeedTemplate(size: 2);
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var trophyNames = Enumerable.Range(0, 3).Select(i => $"Trophy{i}").ToList();
        foreach (var trophyName in trophyNames)
            SeedTrophy(trophyName);
        foreach (var clubName in new[] { "Arsenal", "Barcelona" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyClubMatches(trophyName, clubName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(4));
        Assert.That(instance.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Trophy),
            "SelectPairing must have picked Club x Trophy — Trophy always second, per the Country/Club-first precedent");
        var rowValues = instance.Cells.Select(c => c.RowCategoryValue).Distinct().ToList();
        var colValues = instance.Cells.Select(c => c.ColCategoryValue).Distinct().ToList();
        Assert.That(rowValues, Has.Count.EqualTo(2), "REQ-102: N unique row categories");
        Assert.That(colValues, Has.Count.EqualTo(2), "REQ-102: N unique column categories");
    }
```

**`REQ108_SelectPairing_ExactlyOneTrophySeeded_TrophyPairingNeverSelected`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_SelectPairing_ExactlyOneTrophySeeded_TrophyPairingNeverSelected()
    {
        // Pure mechanism coverage (no longer "matching real seed data" —
        // see the ADR-0061 section below for tests against the actual,
        // now-3-trophy production shape): with only one trophy in the pool
        // and size >= 2, trophyCount(1) can never clear `size` for any mixed
        // pairing, nor `size * 2` for Trophy x Trophy — so every Trophy
        // pairing is infeasible and Country x Club is the only choice,
        // regardless of the injected Random.
        var template = SeedTemplate(size: 2);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedTrophy("Ballon d'Or");
        SeedCachedMatches("France", "Arsenal", 2);
        SeedCachedMatches("France", "Barcelona", 2);
        SeedCachedMatches("Spain", "Arsenal", 2);
        SeedCachedMatches("Spain", "Barcelona", 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.None.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Trophy || c.ColCategoryType == CategoryPairingRules.Trophy),
            "with only one trophy in the pool, Trophy can never be selected for any realistic grid size");
    }
```

**`REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_CountryTrophyPairingIsNowSelectable`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_CountryTrophyPairingIsNowSelectable()
    {
        // The real ReferenceDataSeeder shape as of ADR-0061: exactly three
        // trophies, matching names/flags. Zero clubs seeded -> every
        // Club-involving pairing is infeasible; countryCount(3) and
        // trophyCount(3) both clear the default GridSize = 3, so
        // Country x Trophy is the only feasible pairing — deterministic
        // regardless of the injected Random.
        var template = SeedTemplate(size: 3);
        SeedCountry("France");
        SeedCountry("Spain");
        SeedCountry("Brazil");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var trophyNames = new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" };
        foreach (var countryName in new[] { "France", "Spain", "Brazil" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyCountryMatches(trophyName, countryName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Country && c.ColCategoryType == CategoryPairingRules.Trophy),
            "with the real (now 3-trophy) seeded pool, Country x Trophy must be selectable for a size-3 grid — this reverses S-031's original 'structurally dormant' consequence");
    }
```

**`REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_ClubTrophyPairingIsNowSelectable`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_ClubTrophyPairingIsNowSelectable()
    {
        // Mirror of the Country x Trophy test above — zero countries seeded
        // -> every Country-involving pairing is infeasible, leaving
        // Club x Trophy as the only feasible pairing.
        var template = SeedTemplate(size: 3);
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        SeedClub("Real Madrid");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var trophyNames = new[] { "Ballon d'Or", "FIFA World Cup", "UEFA Champions League" };
        foreach (var clubName in new[] { "Arsenal", "Barcelona", "Real Madrid" })
            foreach (var trophyName in trophyNames)
                SeedCachedTrophyClubMatches(trophyName, clubName, count: 2);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.All.Matches<GridCell>(
            c => c.RowCategoryType == CategoryPairingRules.Club && c.ColCategoryType == CategoryPairingRules.Trophy),
            "with the real (now 3-trophy) seeded pool, Club x Trophy must be selectable for a size-3 grid");
    }
```

**`REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_TrophyTrophyPairingStillInfeasible`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public void REQ108_SelectPairing_MatchingRealSeedDataTrophyCount_ThreeTrophiesSeeded_TrophyTrophyPairingStillInfeasible()
    {
        // Trophy x Trophy needs trophyCount >= size * 2 = 6 — three trophies
        // still doesn't clear that, even though it now clears the plain
        // `>= size` bar Country x Trophy/Club x Trophy need. Zero countries
        // and zero clubs seeded, so no other pairing is feasible either —
        // GenerateInstanceAsync must abort with GridGenerationException
        // rather than silently produce a Trophy x Trophy grid.
        var template = SeedTemplate(size: 3);
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        var service = BuildService(minValidAnswers: 2, maxAttempts: 20);

        Assert.ThrowsAsync<GridGenerationException>(async () =>
            await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));
    }
```

**`REQ109_GenerateInstanceAsync_OnlyUsesValuesFromReferenceTables_NeverFromPlayerAttributeAlone`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ109_GenerateInstanceAsync_OnlyUsesValuesFromReferenceTables_NeverFromPlayerAttributeAlone()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("France", "Arsenal", 3);
        // "PhantomClub" has abundant matching data in PlayerAttribute but was
        // never added as a ClubDefinition row — it must never be considered
        // as a candidate, however good its match count.
        SeedCachedMatches("France", "PhantomClub", 10);
        var service = BuildService(minValidAnswers: 1, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells[0].ColCategoryValue, Is.EqualTo("Arsenal"));
        Assert.That(instance.Cells.Select(c => c.ColCategoryValue), Does.Not.Contain("PhantomClub"));
    }
```

**`REQ109_GenerateInstanceAsync_NullWikidataQid_DoesNotThrow_AndDiscardsThroughOrdinaryRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ109_GenerateInstanceAsync_NullWikidataQid_DoesNotThrow_AndDiscardsThroughOrdinaryRetry()
    {
        var template = SeedTemplate(size: 1);
        // No resolved WikidataQid yet (REQ-109) — must not crash generation.
        SeedCountry("Ruritania", wikidataQid: null);
        SeedClub("NoDataClub");   // cache miss; live lookup is skipped (null country QID) -> 0 matches, discarded
        SeedClub("GoodClub");     // cache hit -> accepted without ever needing a live lookup
        SeedCachedMatches("Ruritania", "GoodClub", 2);
        // Configured on the fake, but unreachable via the real contract since
        // the country QID is null — proves the service never gets a match for
        // "NoDataClub" from this path, only from the (absent) cache.
        _wikidataLookupService.SetMatches("Ruritania", "NoDataClub", BuildFakeLivePlayers("NoDataClub", 5));
        var service = BuildService(minValidAnswers: 2, maxAttempts: 5);

        GameInstance? result = null;
        Assert.DoesNotThrowAsync(async () =>
            result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result!.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells[0].RowCategoryValue, Is.EqualTo("Ruritania"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("GoodClub"));
    }
```

**`REQ114_GenerateInstanceAsync_NationalTeamCountry_PairsWithClubsExactlyLikeAnyOtherCountry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ114_GenerateInstanceAsync_NationalTeamCountry_PairsWithClubsExactlyLikeAnyOtherCountry()
    {
        // No special-casing needed anywhere in grid generation's pairing
        // logic (SelectPairing/CategoryPairingRules) — a flagged country is
        // just another CountryDefinition row.
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        SeedCachedMatches("England", "Tottenham Hotspur", 3);
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        var result = await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.Cells, Has.Count.EqualTo(1));
        Assert.That(instance.Cells[0].RowCategoryType, Is.EqualTo(CategoryPairingRules.Country));
        Assert.That(instance.Cells[0].RowCategoryValue, Is.EqualTo("England"));
        Assert.That(instance.Cells[0].ColCategoryValue, Is.EqualTo("Tottenham Hotspur"));
    }
```

**`REQ114_GenerateInstanceAsync_OrdinaryCountry_StillDispatchesWithFlagFalse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ114_GenerateInstanceAsync_OrdinaryCountry_StillDispatchesWithFlagFalse()
    {
        // The existing P27 path (represented here by
        // UsesCountryForSportProperty = false reaching the lookup service)
        // must stay completely unaffected — this is generation's cache-miss
        // path (GetMatchCountAsync), not the guess-time fallback.
        var template = SeedTemplate(size: 1);
        SeedCountry("France"); // usesCountryForSportProperty defaults to false
        SeedClub("Arsenal");
        // No SeedCachedMatches call — forces the live-lookup path so
        // LookupAndPersistAsync is actually invoked and its flag captured.
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakeLivePlayers("France-Arsenal", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("France", "Arsenal"), Is.False);
    }
```

**`REQ114_GenerateInstanceAsync_NationalTeamCountry_LiveLookupDispatchedWithFlagTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ114_GenerateInstanceAsync_NationalTeamCountry_LiveLookupDispatchedWithFlagTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        // No SeedCachedMatches call — forces the live-lookup path
        // (GetMatchCountAsync's cache miss) so LookupAndPersistAsync is
        // actually invoked and its flag captured.
        _wikidataLookupService.SetMatches("England", "Tottenham Hotspur", BuildFakeLivePlayers("England-Spurs", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("England", "Tottenham Hotspur"), Is.True,
            "CategoryCandidate must carry CountryDefinition.UsesCountryForSportProperty through to the live-lookup dispatch site");
    }
```

**`REQ108_GenerateInstanceAsync_NationalTeamCountryTrophyPairing_LiveLookupDispatchedWithUsesCountryForSportPropertyTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_NationalTeamCountryTrophyPairing_LiveLookupDispatchedWithUsesCountryForSportPropertyTrue()
    {
        // size=1 keeps this deterministic without needing a 3-trophy pool:
        // Country x Club is infeasible (zero clubs seeded), Trophy x Trophy
        // needs trophyCount >= 2, so Country x Trophy is the only feasible
        // pairing with one trophy seeded.
        var template = SeedTemplate(size: 1);
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedTrophy("Ballon d'Or");
        // No SeedCachedTrophyCountryMatches call — forces the live-lookup
        // path so LookupAndPersistTrophyCountryAsync is actually invoked and
        // its flags captured.
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "England", BuildFakeLivePlayers("BallonDor-England", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastUsesCountryForSportProperty("Ballon d'Or", "England"), Is.True,
            "CategoryCandidate must carry CountryDefinition.UsesCountryForSportProperty through to the Trophy x Country live-lookup dispatch site, not silently fall back to P27 (ADR-0035/ADR-0061)");
    }
```

**`REQ108_GenerateInstanceAsync_OrdinaryCountryTrophyPairing_StillDispatchesWithUsesCountryForSportPropertyFalse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_OrdinaryCountryTrophyPairing_StillDispatchesWithUsesCountryForSportPropertyFalse()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France"); // usesCountryForSportProperty defaults to false
        SeedTrophy("Ballon d'Or");
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", BuildFakeLivePlayers("BallonDor-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastUsesCountryForSportProperty("Ballon d'Or", "France"), Is.False);
    }
```

**`REQ108_GenerateInstanceAsync_TeamTrophyCountryPairing_LiveLookupDispatchedWithIsTeamTrophyTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_TeamTrophyCountryPairing_LiveLookupDispatchedWithIsTeamTrophyTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedTrophy("FIFA World Cup", isTeamTrophy: true);
        _wikidataLookupService.SetTrophyCountryMatches("FIFA World Cup", "France", BuildFakeLivePlayers("WorldCup-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastIsTeamTrophy("FIFA World Cup", "France"), Is.True,
            "CategoryCandidate must carry TrophyDefinition.IsTeamTrophy through to the Trophy x Country live-lookup dispatch site (ADR-0061)");
    }
```

**`REQ108_GenerateInstanceAsync_IndividualAwardCountryPairing_StillDispatchesWithIsTeamTrophyFalse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_IndividualAwardCountryPairing_StillDispatchesWithIsTeamTrophyFalse()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedTrophy("Ballon d'Or", isTeamTrophy: false);
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", BuildFakeLivePlayers("BallonDor-France", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyCountryLastIsTeamTrophy("Ballon d'Or", "France"), Is.False);
    }
```

**`REQ108_GenerateInstanceAsync_TeamTrophyClubPairing_LiveLookupDispatchedWithIsTeamTrophyTrue`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs`):
```csharp
[Test]
    public async Task REQ108_GenerateInstanceAsync_TeamTrophyClubPairing_LiveLookupDispatchedWithIsTeamTrophyTrue()
    {
        var template = SeedTemplate(size: 1);
        SeedClub("Real Madrid");
        SeedTrophy("UEFA Champions League", isTeamTrophy: true);
        _wikidataLookupService.SetTrophyClubMatches("UEFA Champions League", "Real Madrid", BuildFakeLivePlayers("ChampionsLeague-RealMadrid", 3));
        var service = BuildService(minValidAnswers: 3, maxAttempts: 5);

        await service.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        Assert.That(_wikidataLookupService.GetTrophyClubLastIsTeamTrophy("UEFA Champions League", "Real Madrid"), Is.True,
            "CategoryCandidate must carry TrophyDefinition.IsTeamTrophy through to the Trophy x Club live-lookup dispatch site (ADR-0061)");
    }
```

### REQ/ADR references and comments within the method

References found: ADR-0061, REQ-102, REQ-107, REQ-109

Inline comments within the method:
```
// REQ-109: candidate values only ever come from the reference
// tables, never derived ad hoc from PlayerAttribute.
// ADR-0061: t.IsTeamTrophy threaded through the same way
// c.UsesCountryForSportProperty is above — see CategoryCandidate's
// own doc comment.
// REQ-102: N unique row categories. Any candidate is a valid row
// header on its own — REQ-107's ban only bites once paired with a
// column, checked inside PickHeadersAsync below.
// REQ-102's "no row category may be identical to a column category"
// only bites when both axes share a category type (Club x Club) —
// Country and Club values can never collide by name.
// GridInstanceId set explicitly rather than left to EF Core's
// relationship fixup via this navigation — Guid is non-nullable,
// so an unset value would be Guid.Empty, not an obviously-wrong
// placeholder EF would know to overwrite.
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 60

**File:** `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`  
**Mutated line(s):** 145-145  
**Mutator:** Equality mutation

**Original:**
```csharp
            (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),
```
**Mutated replacement:**
```csharp
trophyCount > size * 2
```

### Containing method: `GridGenerationService` (lines 28-361, 334 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

**Leading doc comment:**
```
// S-119 (pure refactor, no behavior change): split out of GridGameModule.
//
// Tier 0 scope (MVP-SCOPE.md): grids are Country x Club, Club x Club (as of
// docs/backlog.md S-030), or, as of S-031 (REQ-108), a Trophy-involving
// pairing (Country x Trophy, Club x Trophy, or Trophy x Trophy) — never
// Country x Country (REQ-107). Which pairing a given instance uses is
// picked once per call (SelectPairing), uniformly at random among whichever
// pairings the seeded reference data can support. Row/column headers are then fixed
// once chosen (REQ-102's "N unique row categories and N unique column
// categories") — rows are picked first (any candidate satisfies REQ-107 on
// its own, since the ban only applies to a Country/Country pairing), then
// columns are picked one at a time, each candidate validated against every
// already-fixed row header before being accepted (REQ-101). A rejected
// candidate is discarded and a new one tried, up to
// GridGenerationOptions.MaxAttempts total attempts (a rarely-hit backstop)
// or GridGenerationOptions.MaxDuration of wall-clock time (ADR-0023 — this
// is what actually bounds a real run, well under any infrastructure
// request timeout) — whichever trips first aborts with GridGenerationException,
// matching REQ-101's abort rule.
```

```csharp
   28| public class GridGenerationService(
   29|     IGridInstanceRepository gridInstanceRepository,
   30|     ICategoryValueRepository categoryValueRepository,
   31|     IPlayerAttributeRepository playerAttributeRepository,
   32|     IGridLiveLookupDispatcher liveLookupDispatcher,
   33|     GridGenerationOptions options,
   34|     ILogger<GridGenerationService> logger,
   35|     Random? random = null,
   36|     TimeProvider? timeProvider = null) : IGridGenerationService
   37| {
   38|     // SelectPairing's uniform-at-random choice among every feasible pairing
   39|     // goes through this field — candidate-order shuffling still uses
   40|     // Random.Shared, same as before S-030, since no test relies on
   41|     // controlling shuffle order. Optional constructor param (like
   42|     // WikidataClient's queryTimeout) so tests can pin the pairing choice
   43|     // without DI needing to register a Random.
   44|     private readonly Random _random = random ?? Random.Shared;
   45| 
   46|     // ADR-0023: PickHeadersAsync's own wall-clock deadline reads this
   47|     // rather than DateTime.UtcNow directly, so tests can exercise the
   48|     // deadline-abort branch deterministically. Falls back to the real
   49|     // clock in production the same way RoundGenerationService's
   50|     // TimeProvider does — already registered as TimeProvider.System in
   51|     // Program.cs's DI container, resolved automatically.
   52|     private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
   53| 
   54|     public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
   55|     {
   56|         var template = await gridInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
   57|             ?? throw new GridGenerationException($"GridTemplate '{config.TemplateId}' not found.");
   58| 
   59|         // REQ-109: candidate values only ever come from the reference
   60|         // tables, never derived ad hoc from PlayerAttribute.
   61|         var countries = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
   62|             .Select(c => new CategoryCandidate(c.Name, c.WikidataQid, c.UsesCountryForSportProperty)).ToList();
   63|         var clubs = (await categoryValueRepository.GetClubsAsync(cancellationToken))
   64|             .Select(c => new CategoryCandidate(c.Name, c.WikidataQid)).ToList();
   65|         // ADR-0061: t.IsTeamTrophy threaded through the same way
   66|         // c.UsesCountryForSportProperty is above — see CategoryCandidate's
   67|         // own doc comment.
   68|         var trophies = (await categoryValueRepository.GetTrophiesAsync(cancellationToken))
   69|             .Select(t => new CategoryCandidate(t.Name, t.WikidataQid, IsTeamTrophy: t.IsTeamTrophy)).ToList();
   70| 
   71|         var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);
   72| 
   73|         var rowPool = PoolFor(rowCategoryType, countries, clubs, trophies);
   74|         var colPool = PoolFor(colCategoryType, countries, clubs, trophies);
   75| 
   76|         // REQ-102: N unique row categories. Any candidate is a valid row
   77|         // header on its own — REQ-107's ban only bites once paired with a
   78|         // column, checked inside PickHeadersAsync below.
   79|         var rowHeaders = Shuffle(rowPool).Take(template.Size).ToList();
   80| 
   81|         // REQ-102's "no row category may be identical to a column category"
   82|         // only bites when both axes share a category type (Club x Club) —
   83|         // Country and Club values can never collide by name.
   84|         var colCandidatePool = rowCategoryType == colCategoryType
   85|             ? colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
   86|             : colPool;
   87| 
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
   89| 
   90|         var instanceId = Guid.NewGuid();
   91|         var instance = new GridInstance
   92|         {
   93|             Id = instanceId,
   94|             TemplateId = template.Id,
   95|             // GridInstanceId set explicitly rather than left to EF Core's
   96|             // relationship fixup via this navigation — Guid is non-nullable,
   97|             // so an unset value would be Guid.Empty, not an obviously-wrong
   98|             // placeholder EF would know to overwrite.
   99|             Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
  100|         };
  101|         await gridInstanceRepository.AddInstanceAsync(instance, cancellationToken);
  102| 
  103|         return new GameInstance { Id = instance.Id };
  104|     }
  105| 
  106|     // REQ-107/REQ-108 (S-030, extended S-031): Country x Country is never a
  107|     // candidate, so there's nothing to filter out here, only to choose
  108|     // between. Every other pairing CategoryPairingRules.IsAllowedPairing
  109|     // permits is a candidate: Country x Club, Club x Club, Country x Trophy,
  110|     // Club x Trophy, and Trophy x Trophy — Trophy is always kept as the
  111|     // *second* type in a mixed pairing (Country/Club always first), the
  112|     // same precedent Country x Club already set for Country preceding Club.
  113|     // A same-type pairing (Club x Club, Trophy x Trophy) needs 2xSize
  114|     // distinct values, since REQ-102 forbids a value appearing on both axes;
  115|     // a mixed pairing just needs >= size in each of the two pools. Chooses
  116|     // uniformly at random among whichever pairings the seeded reference
  117|     // data can actually support — generalizing S-030's two-way coin flip to
  118|     // an N-way choice.
  119|     //
  120|     // Non-obvious consequence, load-bearing for what actually ships (see
  121|     // ReferenceDataSeeder and docs/backlog.md S-031): with only one trophy
  122|     // seeded (Ballon d'Or), trophyCount(1) used to be smaller than `size`
  123|     // for any realistic grid size, so every Trophy pairing below was
  124|     // infeasible in production — that was expected, not a bug (REQ-108
  125|     // describes the trophy list as reference data meant to grow later, "a
  126|     // data change, not a code change"), and this class's unit tests proved
  127|     // the mechanism itself worked using a larger injected trophy pool, ahead
  128|     // of production data actually triggering it.
  129|     //
  130|     // UPDATE (ADR-0061, 2026-08-09): ReferenceDataSeeder now seeds three
  131|     // trophies (Ballon d'Or, FIFA World Cup, UEFA Champions League), which
  132|     // makes trophyCount(3) >= size for the default GridSize = 3 — Country x
  133|     // Trophy and Club x Trophy are REACHABLE in production now, for the
  134|     // first time, not just a mechanism proven by tests. Trophy x Trophy
  135|     // still needs trophyCount >= size * 2 = 6, so it remains infeasible for
  136|     // now — this will need revisiting if/when the trophy pool grows further.
  137|     private (string RowType, string ColType) SelectPairing(int size, int countryCount, int clubCount, int trophyCount)
  138|     {
  139|         var candidates = new (string RowType, string ColType, bool Feasible)[]
  140|         {
  141|             (CategoryPairingRules.Country, CategoryPairingRules.Club, countryCount >= size && clubCount >= size),
  142|             (CategoryPairingRules.Club, CategoryPairingRules.Club, clubCount >= size * 2),
  143|             (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
  144|             (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
  145|             (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),
  146|         };
  147| 
  148|         var feasible = candidates.Where(c => c.Feasible).Select(c => (c.RowType, c.ColType)).ToList();
  149| 
  150|         if (feasible.Count == 0)
  151|         {
  152|             throw new GridGenerationException(
  153|                 $"Not enough reference data to build a {size}x{size} grid " +
  154|                 $"({countryCount} countries, {clubCount} clubs, {trophyCount} trophies available).");
  155|         }
  156| 
  157|         return feasible[_random.Next(feasible.Count)];
  158|     }
  159| 
  160|     // PlayerAttribute.AttributeType's reference-table equivalent — which
  161|     // seeded pool a given category type's candidates are drawn from.
  162|     // Distinct from CategoryPairingRules.MapAttributeType (that one maps to
  163|     // PlayerAttribute's vocabulary for guess-checking; this one picks a
  164|     // CategoryCandidate pool for generation).
  165|     private static List<CategoryCandidate> PoolFor(
  166|         string categoryType, List<CategoryCandidate> countries, List<CategoryCandidate> clubs, List<CategoryCandidate> trophies) =>
  167|         categoryType switch
  168|         {
  169|             CategoryPairingRules.Country => countries,
  170|             CategoryPairingRules.Club => clubs,
  171|             CategoryPairingRules.Trophy => trophies,
  172|             _ => throw new GridGenerationException($"Unknown category type '{categoryType}'."),
  173|         };
  174| 
  175|     // REQ-101/107: tries column candidates one at a time (never repeating a
  176|     // rejected one), accepting only those valid against every fixed row
  177|     // header, until N columns are accepted or one of three abort conditions
  178|     // trips: the candidate pool is exhausted, MaxAttempts is hit (a
  179|     // backstop that rarely matters in practice — see its own doc comment),
  180|     // or MaxDuration elapses (ADR-0023 — this is what actually bounds a
  181|     // real run's wall-clock time, well under any infrastructure request
  182|     // timeout, so the caller always gets a definitive answer — success or a
  183|     // clean GridGenerationException — instead of the request being killed
  184|     // out from under it). Generalized by S-030 to work for any pairing of
  185|     // category types, not just Country rows x Club columns.
  186|     //
  187|     // Deliberately still sequential, not concurrent, despite each
  188|     // candidate's live-lookup cost being the dominant source of latency —
  189|     // PlayerStoreRepository/CategoryValueRepository/WikidataLookupService
  190|     // all share one request-scoped XGArcadeDbContext (Program.cs's
  191|     // AddDbContext/AddScoped registrations), and EF Core's DbContext isn't
  192|     // safe for concurrent use by a single instance. Running candidates
  193|     // through Task.WhenAll here would intermittently throw against real
  194|     // Npgsql ("a second operation was started on this context before a
  195|     // previous operation completed") while quietly working against the
  196|     // InMemory provider tests use — exactly the kind of bug that looks
  197|     // fine in CI and breaks in production. Real concurrency would need
  198|     // IDbContextFactory-based per-call contexts threaded through all three
  199|     // components, which is real, valuable follow-up work but a separate,
  200|     // carefully-scoped change, not part of this fix (see ADR-0023).
  201|     private async Task<List<(CategoryCandidate Candidate, int[] MatchCounts)>> PickHeadersAsync(
  202|         string rowCategoryType,
  203|         IReadOnlyList<CategoryCandidate> rowHeaders,
  204|         string colCategoryType,
  205|         IReadOnlyList<CategoryCandidate> colCandidatePool,
  206|         CancellationToken cancellationToken)
  207|     {
  208|         // REQ-107: checked once, before any matching-count query — every
  209|         // column candidate in this call pairs the same two category types
  210|         // (including a Trophy pairing, S-031 — still fixed for the whole
  211|         // call, never varying per candidate), so this is invariant per
  212|         // call. A hypothetical future grid whose row/column category types
  213|         // vary *within* one call would need to check this per candidate
  214|         // instead.
  215|         if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
  216|             throw new GridGenerationException("Country x Country pairing is never allowed (REQ-107).");
  217| 
  218|         var remaining = Shuffle(colCandidatePool);
  219|         var accepted = new List<(CategoryCandidate, int[])>();
  220|         var attempts = 0;
  221|         var deadline = _timeProvider.GetUtcNow() + options.MaxDuration;
  222| 
  223|         logger.LogInformation(
  224|             "Picking {Needed} {ColCategoryType} headers against {RowCategoryType} rows from a pool of {PoolSize} candidates (MaxDuration={MaxDuration}).",
  225|             rowHeaders.Count, colCategoryType, rowCategoryType, remaining.Count, options.MaxDuration);
  226| 
  227|         while (accepted.Count < rowHeaders.Count)
  228|         {
  229|             EnsurePickingCanContinue(remaining.Count, attempts, accepted.Count, rowHeaders.Count, deadline);
  230| 
  231|             var candidate = remaining[^1];
  232|             remaining.RemoveAt(remaining.Count - 1);
  233|             attempts++;
  234| 
  235|             var matchCounts = await TryComputeMatchCountsAsync(rowCategoryType, rowHeaders, colCategoryType, candidate, cancellationToken);
  236|             if (matchCounts is null)
  237|             {
  238|                 logger.LogDebug("Rejected {ColCategoryType} candidate '{Candidate}' — below MinValidAnswers on at least one row.",
  239|                     colCategoryType, candidate.Name);
  240|                 continue;
  241|             }
  242| 
  243|             logger.LogDebug("Accepted {ColCategoryType} candidate '{Candidate}' ({Accepted}/{Needed}).",
  244|                 colCategoryType, candidate.Name, accepted.Count + 1, rowHeaders.Count);
  245|             accepted.Add((candidate, matchCounts));
  246|         }
  247| 
  248|         return accepted;
  249|     }
  250| 
  251|     // PickHeadersAsync's three abort conditions (pool exhausted, MaxAttempts
  252|     // hit, MaxDuration elapsed), unchanged from the original inline checks —
  253|     // same order, same exception messages, still whichever trips first.
  254|     private void EnsurePickingCanContinue(int remainingCount, int attempts, int acceptedCount, int neededCount, DateTimeOffset deadline)
  255|     {
  256|         if (remainingCount == 0)
  257|             throw new GridGenerationException("Ran out of candidates before completing the grid.");
  258|         if (attempts >= options.MaxAttempts)
  259|             throw new GridGenerationException($"Grid generation aborted after {attempts} attempts.");
  260|         if (_timeProvider.GetUtcNow() >= deadline)
  261|             ThrowDeadlineExceeded(remainingCount, attempts, acceptedCount, neededCount);
  262|     }
  263| 
  264|     private void ThrowDeadlineExceeded(int remainingCount, int attempts, int acceptedCount, int neededCount)
  265|     {
  266|         logger.LogWarning(
  267|             "Grid generation aborted after exceeding MaxDuration ({MaxDuration}): {Accepted}/{Needed} headers " +
  268|             "found in {Attempts} attempts, {Remaining} candidates left untried.",
  269|             options.MaxDuration, acceptedCount, neededCount, attempts, remainingCount);
  270|         throw new GridGenerationException(
  271|             $"Grid generation aborted after exceeding {options.MaxDuration} " +
  272|             $"(found {acceptedCount}/{neededCount} valid headers in {attempts} attempts).");
  273|     }
  274| 
  275|     // The inner per-candidate validity check PickHeadersAsync's while loop
  276|     // runs against every fixed row header — null means this candidate is
  277|     // rejected (below MinValidAnswers against at least one row), matching
  278|     // the original inline for-loop's early break exactly, just out of the
  279|     // caller's way.
  280|     private async Task<int[]?> TryComputeMatchCountsAsync(
  281|         string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
  282|         string colCategoryType, CategoryCandidate candidate, CancellationToken cancellationToken)
  283|     {
  284|         var matchCounts = new int[rowHeaders.Count];
  285|         for (var i = 0; i < rowHeaders.Count; i++)
  286|         {
  287|             matchCounts[i] = await GetMatchCountAsync(rowCategoryType, rowHeaders[i], colCategoryType, candidate, cancellationToken);
  288|             if (matchCounts[i] < options.MinValidAnswers)
  289|                 return null;
  290|         }
  291| 
  292|         return matchCounts;
  293|     }
  294| 
  295|     // REQ-103/REQ-109 waterfall (Tier 0: Wikidata-only half, S-006): a local
  296|     // cache miss triggers a live lookup, persisted immediately (never
  297|     // deferred/batched) as WikidataLookupOrigin.Sync — a routine query
  298|     // against Wikidata's own vetted per-category intersection. As of
  299|     // ADR-0032 this origin and REQ-211's narrower guess-time fallback (owned
  300|     // by GridLiveLookupDispatcher) both persist as "verified" (ADR-0029 had
  301|     // trusted only this one as ground truth; ADR-0032 reversed that split),
  302|     // but the two origins are still passed through distinctly for
  303|     // logging/future re-differentiation — see ADR-0032. A category value
  304|     // with no resolved WikidataQid is not an error — the live lookup just
  305|     // returns no matches (REQ-109), which this treats as an ordinary
  306|     // 0-count, handled by the caller's normal retry logic.
  307|     private async Task<int> GetMatchCountAsync(
  308|         string rowCategoryType, CategoryCandidate row,
  309|         string colCategoryType, CategoryCandidate col,
  310|         CancellationToken cancellationToken)
  311|     {
  312|         var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  313|             CategoryPairingRules.MapAttributeType(rowCategoryType), row.Name,
  314|             CategoryPairingRules.MapAttributeType(colCategoryType), col.Name, cancellationToken);
  315|         if (cachedCount > 0)
  316|             return cachedCount;
  317| 
  318|         var liveMatches = await liveLookupDispatcher.LookupMatchesAsync(
  319|             rowCategoryType, row, colCategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
  320|         return liveMatches?.Count ?? 0;
  321|     }
  322| 
  323|     private static List<GridCell> BuildCells(
  324|         Guid gridInstanceId,
  325|         string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
  326|         string colCategoryType, IReadOnlyList<(CategoryCandidate Candidate, int[] MatchCounts)> columns)
  327|     {
  328|         var cells = new List<GridCell>(rowHeaders.Count * columns.Count);
  329|         for (var row = 0; row < rowHeaders.Count; row++)
  330|         {
  331|             for (var col = 0; col < columns.Count; col++)
  332|             {
  333|                 cells.Add(CreateCell(
  334|                     gridInstanceId, row, rowCategoryType, rowHeaders[row], col, colCategoryType, columns[col].Candidate));
  335|             }
  336|         }
  337|         return cells;
  338|     }
  339| 
  340|     private static GridCell CreateCell(
  341|         Guid gridInstanceId, int row, string rowCategoryType, CategoryCandidate rowHeader,
  342|         int col, string colCategoryType, CategoryCandidate colHeader) =>
  343|         new()
  344|         {
  345|             Id = Guid.NewGuid(),
  346|             GridInstanceId = gridInstanceId,
  347|             Row = row,
  348|             Col = col,
  349|             RowCategoryType = rowCategoryType,
  350|             RowCategoryValue = rowHeader.Name,
  351|             ColCategoryType = colCategoryType,
  352|             ColCategoryValue = colHeader.Name,
  353|         };
  354| 
  355|     private static List<T> Shuffle<T>(IReadOnlyList<T> source)
  356|     {
  357|         var array = source.ToArray();
  358|         Random.Shared.Shuffle(array);
  359|         return [.. array];
  360|     }
  361| }
```

### Data flow

**`CategoryPairingRules`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
  108|     // between. Every other pairing CategoryPairingRules.IsAllowedPairing
  141|             (CategoryPairingRules.Country, CategoryPairingRules.Club, countryCount >= size && clubCount >= size),
  142|             (CategoryPairingRules.Club, CategoryPairingRules.Club, clubCount >= size * 2),
  143|             (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
  144|             (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
  145|             (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),  <-- mutation site
  162|     // Distinct from CategoryPairingRules.MapAttributeType (that one maps to
  169|             CategoryPairingRules.Country => countries,
  170|             CategoryPairingRules.Club => clubs,
  171|             CategoryPairingRules.Trophy => trophies,
  215|         if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
  313|             CategoryPairingRules.MapAttributeType(rowCategoryType), row.Name,
  314|             CategoryPairingRules.MapAttributeType(colCategoryType), col.Name, cancellationToken);
```

**`Trophy`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
   12| // docs/backlog.md S-030), or, as of S-031 (REQ-108), a Trophy-involving
   13| // pairing (Country x Trophy, Club x Trophy, or Trophy x Trophy) — never
  109|     // permits is a candidate: Country x Club, Club x Club, Country x Trophy,
  110|     // Club x Trophy, and Trophy x Trophy — Trophy is always kept as the
  113|     // A same-type pairing (Club x Club, Trophy x Trophy) needs 2xSize
  123|     // for any realistic grid size, so every Trophy pairing below was
  133|     // Trophy and Club x Trophy are REACHABLE in production now, for the
  134|     // first time, not just a mechanism proven by tests. Trophy x Trophy
  143|             (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
  144|             (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
  145|             (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),  <-- mutation site
  171|             CategoryPairingRules.Trophy => trophies,
  210|         // (including a Trophy pairing, S-031 — still fixed for the whole
```

**`trophyCount`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
  122|     // seeded (Ballon d'Or), trophyCount(1) used to be smaller than `size`
  132|     // makes trophyCount(3) >= size for the default GridSize = 3 — Country x
  135|     // still needs trophyCount >= size * 2 = 6, so it remains infeasible for
  137|     private (string RowType, string ColType) SelectPairing(int size, int countryCount, int clubCount, int trophyCount)
  143|             (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
  144|             (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
  145|             (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),  <-- mutation site
  154|                 $"({countryCount} countries, {clubCount} clubs, {trophyCount} trophies available).");
```

### Tests

no test matched by name (searched `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs` for tests referencing `GridGenerationService`)

### REQ/ADR references and comments within the method

References found: ADR-0023, ADR-0029, ADR-0032, ADR-0061, REQ-101, REQ-102, REQ-103, REQ-107, REQ-108, REQ-109, REQ-211

Inline comments within the method:
```
// SelectPairing's uniform-at-random choice among every feasible pairing
// goes through this field — candidate-order shuffling still uses
// Random.Shared, same as before S-030, since no test relies on
// controlling shuffle order. Optional constructor param (like
// WikidataClient's queryTimeout) so tests can pin the pairing choice
// without DI needing to register a Random.
// ADR-0023: PickHeadersAsync's own wall-clock deadline reads this
// rather than DateTime.UtcNow directly, so tests can exercise the
// deadline-abort branch deterministically. Falls back to the real
// clock in production the same way RoundGenerationService's
// TimeProvider does — already registered as TimeProvider.System in
// Program.cs's DI container, resolved automatically.
// REQ-109: candidate values only ever come from the reference
// tables, never derived ad hoc from PlayerAttribute.
// ADR-0061: t.IsTeamTrophy threaded through the same way
// c.UsesCountryForSportProperty is above — see CategoryCandidate's
// own doc comment.
// REQ-102: N unique row categories. Any candidate is a valid row
// header on its own — REQ-107's ban only bites once paired with a
// column, checked inside PickHeadersAsync below.
// REQ-102's "no row category may be identical to a column category"
// only bites when both axes share a category type (Club x Club) —
// Country and Club values can never collide by name.
// GridInstanceId set explicitly rather than left to EF Core's
// relationship fixup via this navigation — Guid is non-nullable,
// so an unset value would be Guid.Empty, not an obviously-wrong
// placeholder EF would know to overwrite.
// REQ-107/REQ-108 (S-030, extended S-031): Country x Country is never a
// candidate, so there's nothing to filter out here, only to choose
// between. Every other pairing CategoryPairingRules.IsAllowedPairing
// permits is a candidate: Country x Club, Club x Club, Country x Trophy,
// Club x Trophy, and Trophy x Trophy — Trophy is always kept as the
// *second* type in a mixed pairing (Country/Club always first), the
// same precedent Country x Club already set for Country preceding Club.
// A same-type pairing (Club x Club, Trophy x Trophy) needs 2xSize
// distinct values, since REQ-102 forbids a value appearing on both axes;
// a mixed pairing just needs >= size in each of the two pools. Chooses
// uniformly at random among whichever pairings the seeded reference
// data can actually support — generalizing S-030's two-way coin flip to
// an N-way choice.
//
// Non-obvious consequence, load-bearing for what actually ships (see
// ReferenceDataSeeder and docs/backlog.md S-031): with only one trophy
// seeded (Ballon d'Or), trophyCount(1) used to be smaller than `size`
// for any realistic grid size, so every Trophy pairing below was
// infeasible in production — that was expected, not a bug (REQ-108
// describes the trophy list as reference data meant to grow later, "a
// data change, not a code change"), and this class's unit tests proved
// the mechanism itself worked using a larger injected trophy pool, ahead
// of production data actually triggering it.
//
// UPDATE (ADR-0061, 2026-08-09): ReferenceDataSeeder now seeds three
// trophies (Ballon d'Or, FIFA World Cup, UEFA Champions League), which
// makes trophyCount(3) >= size for the default GridSize = 3 — Country x
// Trophy and Club x Trophy are REACHABLE in production now, for the
// first time, not just a mechanism proven by tests. Trophy x Trophy
// still needs trophyCount >= size * 2 = 6, so it remains infeasible for
// now — this will need revisiting if/when the trophy pool grows further.
// PlayerAttribute.AttributeType's reference-table equivalent — which
// seeded pool a given category type's candidates are drawn from.
// Distinct from CategoryPairingRules.MapAttributeType (that one maps to
// PlayerAttribute's vocabulary for guess-checking; this one picks a
// CategoryCandidate pool for generation).
// REQ-101/107: tries column candidates one at a time (never repeating a
// rejected one), accepting only those valid against every fixed row
// header, until N columns are accepted or one of three abort conditions
// trips: the candidate pool is exhausted, MaxAttempts is hit (a
// backstop that rarely matters in practice — see its own doc comment),
// or MaxDuration elapses (ADR-0023 — this is what actually bounds a
// real run's wall-clock time, well under any infrastructure request
// timeout, so the caller always gets a definitive answer — success or a
// clean GridGenerationException — instead of the request being killed
// out from under it). Generalized by S-030 to work for any pairing of
// category types, not just Country rows x Club columns.
//
// Deliberately still sequential, not concurrent, despite each
// candidate's live-lookup cost being the dominant source of latency —
// PlayerStoreRepository/CategoryValueRepository/WikidataLookupService
// all share one request-scoped XGArcadeDbContext (Program.cs's
// AddDbContext/AddScoped registrations), and EF Core's DbContext isn't
// safe for concurrent use by a single instance. Running candidates
// through Task.WhenAll here would intermittently throw against real
// Npgsql ("a second operation was started on this context before a
// previous operation completed") while quietly working against the
// InMemory provider tests use — exactly the kind of bug that looks
// fine in CI and breaks in production. Real concurrency would need
// IDbContextFactory-based per-call contexts threaded through all three
// components, which is real, valuable follow-up work but a separate,
// carefully-scoped change, not part of this fix (see ADR-0023).
// REQ-107: checked once, before any matching-count query — every
// column candidate in this call pairs the same two category types
// (including a Trophy pairing, S-031 — still fixed for the whole
// call, never varying per candidate), so this is invariant per
// call. A hypothetical future grid whose row/column category types
// vary *within* one call would need to check this per candidate
// instead.
// PickHeadersAsync's three abort conditions (pool exhausted, MaxAttempts
// hit, MaxDuration elapsed), unchanged from the original inline checks —
// same order, same exception messages, still whichever trips first.
// The inner per-candidate validity check PickHeadersAsync's while loop
// runs against every fixed row header — null means this candidate is
// rejected (below MinValidAnswers against at least one row), matching
// the original inline for-loop's early break exactly, just out of the
// caller's way.
// REQ-103/REQ-109 waterfall (Tier 0: Wikidata-only half, S-006): a local
// cache miss triggers a live lookup, persisted immediately (never
// deferred/batched) as WikidataLookupOrigin.Sync — a routine query
// against Wikidata's own vetted per-category intersection. As of
// ADR-0032 this origin and REQ-211's narrower guess-time fallback (owned
// by GridLiveLookupDispatcher) both persist as "verified" (ADR-0029 had
// trusted only this one as ground truth; ADR-0032 reversed that split),
// but the two origins are still passed through distinctly for
// logging/future re-differentiation — see ADR-0032. A category value
// with no resolved WikidataQid is not an error — the live lookup just
// returns no matches (REQ-109), which this treats as an ordinary
// 0-count, handled by the caller's normal retry logic.
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 61

**File:** `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`  
**Mutated line(s):** 145-145  
**Mutator:** Arithmetic mutation

**Original:**
```csharp
            (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),
```
**Mutated replacement:**
```csharp
size / 2
```

### Containing method: `GridGenerationService` (lines 28-361, 334 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

**Leading doc comment:**
```
// S-119 (pure refactor, no behavior change): split out of GridGameModule.
//
// Tier 0 scope (MVP-SCOPE.md): grids are Country x Club, Club x Club (as of
// docs/backlog.md S-030), or, as of S-031 (REQ-108), a Trophy-involving
// pairing (Country x Trophy, Club x Trophy, or Trophy x Trophy) — never
// Country x Country (REQ-107). Which pairing a given instance uses is
// picked once per call (SelectPairing), uniformly at random among whichever
// pairings the seeded reference data can support. Row/column headers are then fixed
// once chosen (REQ-102's "N unique row categories and N unique column
// categories") — rows are picked first (any candidate satisfies REQ-107 on
// its own, since the ban only applies to a Country/Country pairing), then
// columns are picked one at a time, each candidate validated against every
// already-fixed row header before being accepted (REQ-101). A rejected
// candidate is discarded and a new one tried, up to
// GridGenerationOptions.MaxAttempts total attempts (a rarely-hit backstop)
// or GridGenerationOptions.MaxDuration of wall-clock time (ADR-0023 — this
// is what actually bounds a real run, well under any infrastructure
// request timeout) — whichever trips first aborts with GridGenerationException,
// matching REQ-101's abort rule.
```

```csharp
   28| public class GridGenerationService(
   29|     IGridInstanceRepository gridInstanceRepository,
   30|     ICategoryValueRepository categoryValueRepository,
   31|     IPlayerAttributeRepository playerAttributeRepository,
   32|     IGridLiveLookupDispatcher liveLookupDispatcher,
   33|     GridGenerationOptions options,
   34|     ILogger<GridGenerationService> logger,
   35|     Random? random = null,
   36|     TimeProvider? timeProvider = null) : IGridGenerationService
   37| {
   38|     // SelectPairing's uniform-at-random choice among every feasible pairing
   39|     // goes through this field — candidate-order shuffling still uses
   40|     // Random.Shared, same as before S-030, since no test relies on
   41|     // controlling shuffle order. Optional constructor param (like
   42|     // WikidataClient's queryTimeout) so tests can pin the pairing choice
   43|     // without DI needing to register a Random.
   44|     private readonly Random _random = random ?? Random.Shared;
   45| 
   46|     // ADR-0023: PickHeadersAsync's own wall-clock deadline reads this
   47|     // rather than DateTime.UtcNow directly, so tests can exercise the
   48|     // deadline-abort branch deterministically. Falls back to the real
   49|     // clock in production the same way RoundGenerationService's
   50|     // TimeProvider does — already registered as TimeProvider.System in
   51|     // Program.cs's DI container, resolved automatically.
   52|     private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
   53| 
   54|     public async Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
   55|     {
   56|         var template = await gridInstanceRepository.GetTemplateByIdAsync(config.TemplateId, cancellationToken)
   57|             ?? throw new GridGenerationException($"GridTemplate '{config.TemplateId}' not found.");
   58| 
   59|         // REQ-109: candidate values only ever come from the reference
   60|         // tables, never derived ad hoc from PlayerAttribute.
   61|         var countries = (await categoryValueRepository.GetCountriesAsync(cancellationToken))
   62|             .Select(c => new CategoryCandidate(c.Name, c.WikidataQid, c.UsesCountryForSportProperty)).ToList();
   63|         var clubs = (await categoryValueRepository.GetClubsAsync(cancellationToken))
   64|             .Select(c => new CategoryCandidate(c.Name, c.WikidataQid)).ToList();
   65|         // ADR-0061: t.IsTeamTrophy threaded through the same way
   66|         // c.UsesCountryForSportProperty is above — see CategoryCandidate's
   67|         // own doc comment.
   68|         var trophies = (await categoryValueRepository.GetTrophiesAsync(cancellationToken))
   69|             .Select(t => new CategoryCandidate(t.Name, t.WikidataQid, IsTeamTrophy: t.IsTeamTrophy)).ToList();
   70| 
   71|         var (rowCategoryType, colCategoryType) = SelectPairing(template.Size, countries.Count, clubs.Count, trophies.Count);
   72| 
   73|         var rowPool = PoolFor(rowCategoryType, countries, clubs, trophies);
   74|         var colPool = PoolFor(colCategoryType, countries, clubs, trophies);
   75| 
   76|         // REQ-102: N unique row categories. Any candidate is a valid row
   77|         // header on its own — REQ-107's ban only bites once paired with a
   78|         // column, checked inside PickHeadersAsync below.
   79|         var rowHeaders = Shuffle(rowPool).Take(template.Size).ToList();
   80| 
   81|         // REQ-102's "no row category may be identical to a column category"
   82|         // only bites when both axes share a category type (Club x Club) —
   83|         // Country and Club values can never collide by name.
   84|         var colCandidatePool = rowCategoryType == colCategoryType
   85|             ? colPool.Where(c => rowHeaders.All(r => r.Name != c.Name)).ToList()
   86|             : colPool;
   87| 
   88|         var columns = await PickHeadersAsync(rowCategoryType, rowHeaders, colCategoryType, colCandidatePool, cancellationToken);
   89| 
   90|         var instanceId = Guid.NewGuid();
   91|         var instance = new GridInstance
   92|         {
   93|             Id = instanceId,
   94|             TemplateId = template.Id,
   95|             // GridInstanceId set explicitly rather than left to EF Core's
   96|             // relationship fixup via this navigation — Guid is non-nullable,
   97|             // so an unset value would be Guid.Empty, not an obviously-wrong
   98|             // placeholder EF would know to overwrite.
   99|             Cells = BuildCells(instanceId, rowCategoryType, rowHeaders, colCategoryType, columns),
  100|         };
  101|         await gridInstanceRepository.AddInstanceAsync(instance, cancellationToken);
  102| 
  103|         return new GameInstance { Id = instance.Id };
  104|     }
  105| 
  106|     // REQ-107/REQ-108 (S-030, extended S-031): Country x Country is never a
  107|     // candidate, so there's nothing to filter out here, only to choose
  108|     // between. Every other pairing CategoryPairingRules.IsAllowedPairing
  109|     // permits is a candidate: Country x Club, Club x Club, Country x Trophy,
  110|     // Club x Trophy, and Trophy x Trophy — Trophy is always kept as the
  111|     // *second* type in a mixed pairing (Country/Club always first), the
  112|     // same precedent Country x Club already set for Country preceding Club.
  113|     // A same-type pairing (Club x Club, Trophy x Trophy) needs 2xSize
  114|     // distinct values, since REQ-102 forbids a value appearing on both axes;
  115|     // a mixed pairing just needs >= size in each of the two pools. Chooses
  116|     // uniformly at random among whichever pairings the seeded reference
  117|     // data can actually support — generalizing S-030's two-way coin flip to
  118|     // an N-way choice.
  119|     //
  120|     // Non-obvious consequence, load-bearing for what actually ships (see
  121|     // ReferenceDataSeeder and docs/backlog.md S-031): with only one trophy
  122|     // seeded (Ballon d'Or), trophyCount(1) used to be smaller than `size`
  123|     // for any realistic grid size, so every Trophy pairing below was
  124|     // infeasible in production — that was expected, not a bug (REQ-108
  125|     // describes the trophy list as reference data meant to grow later, "a
  126|     // data change, not a code change"), and this class's unit tests proved
  127|     // the mechanism itself worked using a larger injected trophy pool, ahead
  128|     // of production data actually triggering it.
  129|     //
  130|     // UPDATE (ADR-0061, 2026-08-09): ReferenceDataSeeder now seeds three
  131|     // trophies (Ballon d'Or, FIFA World Cup, UEFA Champions League), which
  132|     // makes trophyCount(3) >= size for the default GridSize = 3 — Country x
  133|     // Trophy and Club x Trophy are REACHABLE in production now, for the
  134|     // first time, not just a mechanism proven by tests. Trophy x Trophy
  135|     // still needs trophyCount >= size * 2 = 6, so it remains infeasible for
  136|     // now — this will need revisiting if/when the trophy pool grows further.
  137|     private (string RowType, string ColType) SelectPairing(int size, int countryCount, int clubCount, int trophyCount)
  138|     {
  139|         var candidates = new (string RowType, string ColType, bool Feasible)[]
  140|         {
  141|             (CategoryPairingRules.Country, CategoryPairingRules.Club, countryCount >= size && clubCount >= size),
  142|             (CategoryPairingRules.Club, CategoryPairingRules.Club, clubCount >= size * 2),
  143|             (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
  144|             (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
  145|             (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),
  146|         };
  147| 
  148|         var feasible = candidates.Where(c => c.Feasible).Select(c => (c.RowType, c.ColType)).ToList();
  149| 
  150|         if (feasible.Count == 0)
  151|         {
  152|             throw new GridGenerationException(
  153|                 $"Not enough reference data to build a {size}x{size} grid " +
  154|                 $"({countryCount} countries, {clubCount} clubs, {trophyCount} trophies available).");
  155|         }
  156| 
  157|         return feasible[_random.Next(feasible.Count)];
  158|     }
  159| 
  160|     // PlayerAttribute.AttributeType's reference-table equivalent — which
  161|     // seeded pool a given category type's candidates are drawn from.
  162|     // Distinct from CategoryPairingRules.MapAttributeType (that one maps to
  163|     // PlayerAttribute's vocabulary for guess-checking; this one picks a
  164|     // CategoryCandidate pool for generation).
  165|     private static List<CategoryCandidate> PoolFor(
  166|         string categoryType, List<CategoryCandidate> countries, List<CategoryCandidate> clubs, List<CategoryCandidate> trophies) =>
  167|         categoryType switch
  168|         {
  169|             CategoryPairingRules.Country => countries,
  170|             CategoryPairingRules.Club => clubs,
  171|             CategoryPairingRules.Trophy => trophies,
  172|             _ => throw new GridGenerationException($"Unknown category type '{categoryType}'."),
  173|         };
  174| 
  175|     // REQ-101/107: tries column candidates one at a time (never repeating a
  176|     // rejected one), accepting only those valid against every fixed row
  177|     // header, until N columns are accepted or one of three abort conditions
  178|     // trips: the candidate pool is exhausted, MaxAttempts is hit (a
  179|     // backstop that rarely matters in practice — see its own doc comment),
  180|     // or MaxDuration elapses (ADR-0023 — this is what actually bounds a
  181|     // real run's wall-clock time, well under any infrastructure request
  182|     // timeout, so the caller always gets a definitive answer — success or a
  183|     // clean GridGenerationException — instead of the request being killed
  184|     // out from under it). Generalized by S-030 to work for any pairing of
  185|     // category types, not just Country rows x Club columns.
  186|     //
  187|     // Deliberately still sequential, not concurrent, despite each
  188|     // candidate's live-lookup cost being the dominant source of latency —
  189|     // PlayerStoreRepository/CategoryValueRepository/WikidataLookupService
  190|     // all share one request-scoped XGArcadeDbContext (Program.cs's
  191|     // AddDbContext/AddScoped registrations), and EF Core's DbContext isn't
  192|     // safe for concurrent use by a single instance. Running candidates
  193|     // through Task.WhenAll here would intermittently throw against real
  194|     // Npgsql ("a second operation was started on this context before a
  195|     // previous operation completed") while quietly working against the
  196|     // InMemory provider tests use — exactly the kind of bug that looks
  197|     // fine in CI and breaks in production. Real concurrency would need
  198|     // IDbContextFactory-based per-call contexts threaded through all three
  199|     // components, which is real, valuable follow-up work but a separate,
  200|     // carefully-scoped change, not part of this fix (see ADR-0023).
  201|     private async Task<List<(CategoryCandidate Candidate, int[] MatchCounts)>> PickHeadersAsync(
  202|         string rowCategoryType,
  203|         IReadOnlyList<CategoryCandidate> rowHeaders,
  204|         string colCategoryType,
  205|         IReadOnlyList<CategoryCandidate> colCandidatePool,
  206|         CancellationToken cancellationToken)
  207|     {
  208|         // REQ-107: checked once, before any matching-count query — every
  209|         // column candidate in this call pairs the same two category types
  210|         // (including a Trophy pairing, S-031 — still fixed for the whole
  211|         // call, never varying per candidate), so this is invariant per
  212|         // call. A hypothetical future grid whose row/column category types
  213|         // vary *within* one call would need to check this per candidate
  214|         // instead.
  215|         if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
  216|             throw new GridGenerationException("Country x Country pairing is never allowed (REQ-107).");
  217| 
  218|         var remaining = Shuffle(colCandidatePool);
  219|         var accepted = new List<(CategoryCandidate, int[])>();
  220|         var attempts = 0;
  221|         var deadline = _timeProvider.GetUtcNow() + options.MaxDuration;
  222| 
  223|         logger.LogInformation(
  224|             "Picking {Needed} {ColCategoryType} headers against {RowCategoryType} rows from a pool of {PoolSize} candidates (MaxDuration={MaxDuration}).",
  225|             rowHeaders.Count, colCategoryType, rowCategoryType, remaining.Count, options.MaxDuration);
  226| 
  227|         while (accepted.Count < rowHeaders.Count)
  228|         {
  229|             EnsurePickingCanContinue(remaining.Count, attempts, accepted.Count, rowHeaders.Count, deadline);
  230| 
  231|             var candidate = remaining[^1];
  232|             remaining.RemoveAt(remaining.Count - 1);
  233|             attempts++;
  234| 
  235|             var matchCounts = await TryComputeMatchCountsAsync(rowCategoryType, rowHeaders, colCategoryType, candidate, cancellationToken);
  236|             if (matchCounts is null)
  237|             {
  238|                 logger.LogDebug("Rejected {ColCategoryType} candidate '{Candidate}' — below MinValidAnswers on at least one row.",
  239|                     colCategoryType, candidate.Name);
  240|                 continue;
  241|             }
  242| 
  243|             logger.LogDebug("Accepted {ColCategoryType} candidate '{Candidate}' ({Accepted}/{Needed}).",
  244|                 colCategoryType, candidate.Name, accepted.Count + 1, rowHeaders.Count);
  245|             accepted.Add((candidate, matchCounts));
  246|         }
  247| 
  248|         return accepted;
  249|     }
  250| 
  251|     // PickHeadersAsync's three abort conditions (pool exhausted, MaxAttempts
  252|     // hit, MaxDuration elapsed), unchanged from the original inline checks —
  253|     // same order, same exception messages, still whichever trips first.
  254|     private void EnsurePickingCanContinue(int remainingCount, int attempts, int acceptedCount, int neededCount, DateTimeOffset deadline)
  255|     {
  256|         if (remainingCount == 0)
  257|             throw new GridGenerationException("Ran out of candidates before completing the grid.");
  258|         if (attempts >= options.MaxAttempts)
  259|             throw new GridGenerationException($"Grid generation aborted after {attempts} attempts.");
  260|         if (_timeProvider.GetUtcNow() >= deadline)
  261|             ThrowDeadlineExceeded(remainingCount, attempts, acceptedCount, neededCount);
  262|     }
  263| 
  264|     private void ThrowDeadlineExceeded(int remainingCount, int attempts, int acceptedCount, int neededCount)
  265|     {
  266|         logger.LogWarning(
  267|             "Grid generation aborted after exceeding MaxDuration ({MaxDuration}): {Accepted}/{Needed} headers " +
  268|             "found in {Attempts} attempts, {Remaining} candidates left untried.",
  269|             options.MaxDuration, acceptedCount, neededCount, attempts, remainingCount);
  270|         throw new GridGenerationException(
  271|             $"Grid generation aborted after exceeding {options.MaxDuration} " +
  272|             $"(found {acceptedCount}/{neededCount} valid headers in {attempts} attempts).");
  273|     }
  274| 
  275|     // The inner per-candidate validity check PickHeadersAsync's while loop
  276|     // runs against every fixed row header — null means this candidate is
  277|     // rejected (below MinValidAnswers against at least one row), matching
  278|     // the original inline for-loop's early break exactly, just out of the
  279|     // caller's way.
  280|     private async Task<int[]?> TryComputeMatchCountsAsync(
  281|         string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
  282|         string colCategoryType, CategoryCandidate candidate, CancellationToken cancellationToken)
  283|     {
  284|         var matchCounts = new int[rowHeaders.Count];
  285|         for (var i = 0; i < rowHeaders.Count; i++)
  286|         {
  287|             matchCounts[i] = await GetMatchCountAsync(rowCategoryType, rowHeaders[i], colCategoryType, candidate, cancellationToken);
  288|             if (matchCounts[i] < options.MinValidAnswers)
  289|                 return null;
  290|         }
  291| 
  292|         return matchCounts;
  293|     }
  294| 
  295|     // REQ-103/REQ-109 waterfall (Tier 0: Wikidata-only half, S-006): a local
  296|     // cache miss triggers a live lookup, persisted immediately (never
  297|     // deferred/batched) as WikidataLookupOrigin.Sync — a routine query
  298|     // against Wikidata's own vetted per-category intersection. As of
  299|     // ADR-0032 this origin and REQ-211's narrower guess-time fallback (owned
  300|     // by GridLiveLookupDispatcher) both persist as "verified" (ADR-0029 had
  301|     // trusted only this one as ground truth; ADR-0032 reversed that split),
  302|     // but the two origins are still passed through distinctly for
  303|     // logging/future re-differentiation — see ADR-0032. A category value
  304|     // with no resolved WikidataQid is not an error — the live lookup just
  305|     // returns no matches (REQ-109), which this treats as an ordinary
  306|     // 0-count, handled by the caller's normal retry logic.
  307|     private async Task<int> GetMatchCountAsync(
  308|         string rowCategoryType, CategoryCandidate row,
  309|         string colCategoryType, CategoryCandidate col,
  310|         CancellationToken cancellationToken)
  311|     {
  312|         var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  313|             CategoryPairingRules.MapAttributeType(rowCategoryType), row.Name,
  314|             CategoryPairingRules.MapAttributeType(colCategoryType), col.Name, cancellationToken);
  315|         if (cachedCount > 0)
  316|             return cachedCount;
  317| 
  318|         var liveMatches = await liveLookupDispatcher.LookupMatchesAsync(
  319|             rowCategoryType, row, colCategoryType, col, WikidataLookupOrigin.Sync, cancellationToken);
  320|         return liveMatches?.Count ?? 0;
  321|     }
  322| 
  323|     private static List<GridCell> BuildCells(
  324|         Guid gridInstanceId,
  325|         string rowCategoryType, IReadOnlyList<CategoryCandidate> rowHeaders,
  326|         string colCategoryType, IReadOnlyList<(CategoryCandidate Candidate, int[] MatchCounts)> columns)
  327|     {
  328|         var cells = new List<GridCell>(rowHeaders.Count * columns.Count);
  329|         for (var row = 0; row < rowHeaders.Count; row++)
  330|         {
  331|             for (var col = 0; col < columns.Count; col++)
  332|             {
  333|                 cells.Add(CreateCell(
  334|                     gridInstanceId, row, rowCategoryType, rowHeaders[row], col, colCategoryType, columns[col].Candidate));
  335|             }
  336|         }
  337|         return cells;
  338|     }
  339| 
  340|     private static GridCell CreateCell(
  341|         Guid gridInstanceId, int row, string rowCategoryType, CategoryCandidate rowHeader,
  342|         int col, string colCategoryType, CategoryCandidate colHeader) =>
  343|         new()
  344|         {
  345|             Id = Guid.NewGuid(),
  346|             GridInstanceId = gridInstanceId,
  347|             Row = row,
  348|             Col = col,
  349|             RowCategoryType = rowCategoryType,
  350|             RowCategoryValue = rowHeader.Name,
  351|             ColCategoryType = colCategoryType,
  352|             ColCategoryValue = colHeader.Name,
  353|         };
  354| 
  355|     private static List<T> Shuffle<T>(IReadOnlyList<T> source)
  356|     {
  357|         var array = source.ToArray();
  358|         Random.Shared.Shuffle(array);
  359|         return [.. array];
  360|     }
  361| }
```

### Data flow

**`CategoryPairingRules`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
  108|     // between. Every other pairing CategoryPairingRules.IsAllowedPairing
  141|             (CategoryPairingRules.Country, CategoryPairingRules.Club, countryCount >= size && clubCount >= size),
  142|             (CategoryPairingRules.Club, CategoryPairingRules.Club, clubCount >= size * 2),
  143|             (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
  144|             (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
  145|             (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),  <-- mutation site
  162|     // Distinct from CategoryPairingRules.MapAttributeType (that one maps to
  169|             CategoryPairingRules.Country => countries,
  170|             CategoryPairingRules.Club => clubs,
  171|             CategoryPairingRules.Trophy => trophies,
  215|         if (!CategoryPairingRules.IsAllowedPairing(rowCategoryType, colCategoryType))
  313|             CategoryPairingRules.MapAttributeType(rowCategoryType), row.Name,
  314|             CategoryPairingRules.MapAttributeType(colCategoryType), col.Name, cancellationToken);
```

**`Trophy`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
   12| // docs/backlog.md S-030), or, as of S-031 (REQ-108), a Trophy-involving
   13| // pairing (Country x Trophy, Club x Trophy, or Trophy x Trophy) — never
  109|     // permits is a candidate: Country x Club, Club x Club, Country x Trophy,
  110|     // Club x Trophy, and Trophy x Trophy — Trophy is always kept as the
  113|     // A same-type pairing (Club x Club, Trophy x Trophy) needs 2xSize
  123|     // for any realistic grid size, so every Trophy pairing below was
  133|     // Trophy and Club x Trophy are REACHABLE in production now, for the
  134|     // first time, not just a mechanism proven by tests. Trophy x Trophy
  143|             (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
  144|             (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
  145|             (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),  <-- mutation site
  171|             CategoryPairingRules.Trophy => trophies,
  210|         // (including a Trophy pairing, S-031 — still fixed for the whole
```

**`trophyCount`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridGenerationService.cs`:
```
  122|     // seeded (Ballon d'Or), trophyCount(1) used to be smaller than `size`
  132|     // makes trophyCount(3) >= size for the default GridSize = 3 — Country x
  135|     // still needs trophyCount >= size * 2 = 6, so it remains infeasible for
  137|     private (string RowType, string ColType) SelectPairing(int size, int countryCount, int clubCount, int trophyCount)
  143|             (CategoryPairingRules.Country, CategoryPairingRules.Trophy, countryCount >= size && trophyCount >= size),
  144|             (CategoryPairingRules.Club, CategoryPairingRules.Trophy, clubCount >= size && trophyCount >= size),
  145|             (CategoryPairingRules.Trophy, CategoryPairingRules.Trophy, trophyCount >= size * 2),  <-- mutation site
  154|                 $"({countryCount} countries, {clubCount} clubs, {trophyCount} trophies available).");
```

### Tests

no test matched by name (searched `backend/tests/XGArcade.Games.XGGrid.Tests/GridGenerationServiceTests.cs` for tests referencing `GridGenerationService`)

### REQ/ADR references and comments within the method

References found: ADR-0023, ADR-0029, ADR-0032, ADR-0061, REQ-101, REQ-102, REQ-103, REQ-107, REQ-108, REQ-109, REQ-211

Inline comments within the method:
```
// SelectPairing's uniform-at-random choice among every feasible pairing
// goes through this field — candidate-order shuffling still uses
// Random.Shared, same as before S-030, since no test relies on
// controlling shuffle order. Optional constructor param (like
// WikidataClient's queryTimeout) so tests can pin the pairing choice
// without DI needing to register a Random.
// ADR-0023: PickHeadersAsync's own wall-clock deadline reads this
// rather than DateTime.UtcNow directly, so tests can exercise the
// deadline-abort branch deterministically. Falls back to the real
// clock in production the same way RoundGenerationService's
// TimeProvider does — already registered as TimeProvider.System in
// Program.cs's DI container, resolved automatically.
// REQ-109: candidate values only ever come from the reference
// tables, never derived ad hoc from PlayerAttribute.
// ADR-0061: t.IsTeamTrophy threaded through the same way
// c.UsesCountryForSportProperty is above — see CategoryCandidate's
// own doc comment.
// REQ-102: N unique row categories. Any candidate is a valid row
// header on its own — REQ-107's ban only bites once paired with a
// column, checked inside PickHeadersAsync below.
// REQ-102's "no row category may be identical to a column category"
// only bites when both axes share a category type (Club x Club) —
// Country and Club values can never collide by name.
// GridInstanceId set explicitly rather than left to EF Core's
// relationship fixup via this navigation — Guid is non-nullable,
// so an unset value would be Guid.Empty, not an obviously-wrong
// placeholder EF would know to overwrite.
// REQ-107/REQ-108 (S-030, extended S-031): Country x Country is never a
// candidate, so there's nothing to filter out here, only to choose
// between. Every other pairing CategoryPairingRules.IsAllowedPairing
// permits is a candidate: Country x Club, Club x Club, Country x Trophy,
// Club x Trophy, and Trophy x Trophy — Trophy is always kept as the
// *second* type in a mixed pairing (Country/Club always first), the
// same precedent Country x Club already set for Country preceding Club.
// A same-type pairing (Club x Club, Trophy x Trophy) needs 2xSize
// distinct values, since REQ-102 forbids a value appearing on both axes;
// a mixed pairing just needs >= size in each of the two pools. Chooses
// uniformly at random among whichever pairings the seeded reference
// data can actually support — generalizing S-030's two-way coin flip to
// an N-way choice.
//
// Non-obvious consequence, load-bearing for what actually ships (see
// ReferenceDataSeeder and docs/backlog.md S-031): with only one trophy
// seeded (Ballon d'Or), trophyCount(1) used to be smaller than `size`
// for any realistic grid size, so every Trophy pairing below was
// infeasible in production — that was expected, not a bug (REQ-108
// describes the trophy list as reference data meant to grow later, "a
// data change, not a code change"), and this class's unit tests proved
// the mechanism itself worked using a larger injected trophy pool, ahead
// of production data actually triggering it.
//
// UPDATE (ADR-0061, 2026-08-09): ReferenceDataSeeder now seeds three
// trophies (Ballon d'Or, FIFA World Cup, UEFA Champions League), which
// makes trophyCount(3) >= size for the default GridSize = 3 — Country x
// Trophy and Club x Trophy are REACHABLE in production now, for the
// first time, not just a mechanism proven by tests. Trophy x Trophy
// still needs trophyCount >= size * 2 = 6, so it remains infeasible for
// now — this will need revisiting if/when the trophy pool grows further.
// PlayerAttribute.AttributeType's reference-table equivalent — which
// seeded pool a given category type's candidates are drawn from.
// Distinct from CategoryPairingRules.MapAttributeType (that one maps to
// PlayerAttribute's vocabulary for guess-checking; this one picks a
// CategoryCandidate pool for generation).
// REQ-101/107: tries column candidates one at a time (never repeating a
// rejected one), accepting only those valid against every fixed row
// header, until N columns are accepted or one of three abort conditions
// trips: the candidate pool is exhausted, MaxAttempts is hit (a
// backstop that rarely matters in practice — see its own doc comment),
// or MaxDuration elapses (ADR-0023 — this is what actually bounds a
// real run's wall-clock time, well under any infrastructure request
// timeout, so the caller always gets a definitive answer — success or a
// clean GridGenerationException — instead of the request being killed
// out from under it). Generalized by S-030 to work for any pairing of
// category types, not just Country rows x Club columns.
//
// Deliberately still sequential, not concurrent, despite each
// candidate's live-lookup cost being the dominant source of latency —
// PlayerStoreRepository/CategoryValueRepository/WikidataLookupService
// all share one request-scoped XGArcadeDbContext (Program.cs's
// AddDbContext/AddScoped registrations), and EF Core's DbContext isn't
// safe for concurrent use by a single instance. Running candidates
// through Task.WhenAll here would intermittently throw against real
// Npgsql ("a second operation was started on this context before a
// previous operation completed") while quietly working against the
// InMemory provider tests use — exactly the kind of bug that looks
// fine in CI and breaks in production. Real concurrency would need
// IDbContextFactory-based per-call contexts threaded through all three
// components, which is real, valuable follow-up work but a separate,
// carefully-scoped change, not part of this fix (see ADR-0023).
// REQ-107: checked once, before any matching-count query — every
// column candidate in this call pairs the same two category types
// (including a Trophy pairing, S-031 — still fixed for the whole
// call, never varying per candidate), so this is invariant per
// call. A hypothetical future grid whose row/column category types
// vary *within* one call would need to check this per candidate
// instead.
// PickHeadersAsync's three abort conditions (pool exhausted, MaxAttempts
// hit, MaxDuration elapsed), unchanged from the original inline checks —
// same order, same exception messages, still whichever trips first.
// The inner per-candidate validity check PickHeadersAsync's while loop
// runs against every fixed row header — null means this candidate is
// rejected (below MinValidAnswers against at least one row), matching
// the original inline for-loop's early break exactly, just out of the
// caller's way.
// REQ-103/REQ-109 waterfall (Tier 0: Wikidata-only half, S-006): a local
// cache miss triggers a live lookup, persisted immediately (never
// deferred/batched) as WikidataLookupOrigin.Sync — a routine query
// against Wikidata's own vetted per-category intersection. As of
// ADR-0032 this origin and REQ-211's narrower guess-time fallback (owned
// by GridLiveLookupDispatcher) both persist as "verified" (ADR-0029 had
// trusted only this one as ground truth; ADR-0032 reversed that split),
// but the two origins are still passed through distinctly for
// logging/future re-differentiation — see ADR-0032. A category value
// with no resolved WikidataQid is not an error — the live lookup just
// returns no matches (REQ-109), which this treats as an ordinary
// 0-count, handled by the caller's normal retry logic.
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 263

**File:** `backend/src/XGArcade.Games.XGGrid/GridNameMatcher.cs`  
**Mutated line(s):** 123-123  
**Mutator:** String mutation

**Original:**
```csharp
            "Guess for cell {CellId} in instance {InstanceId} matched {Count} fitting candidates; " +
```
**Mutated replacement:**
```csharp
""
```

### Containing method: `AcceptMatchAsync` (lines 105-129, 25 lines)

**Leading doc comment:**
```
    // REQ-209: exactly one fitting candidate is accepted automatically; more
    // than one raises a disambiguation prompt instead of guessing on the
    // player's behalf. Shared by every stage of FindMatchAsync above so this
    // rule can't drift between the exact/alias/fuzzy paths.
    //
    // chosenPlayerId fast path (REQ-209/REQ-210): when set, this is a
    // resubmission answering a prompt raised earlier in the same attempt —
    // skip straight to verifying that specific player is (a) among this
    // run's `matching` candidates for whichever stage produced them and
    // (b) therefore still satisfies both categories right now (membership in
    // a freshly-computed `matching` list proves both at once — never trust
    // the client-supplied id blindly). A chosenPlayerId that doesn't
    // validate — not in the matching set any more, or matching is empty —
    // is treated as an ordinary incorrect guess, never thrown, same
    // fail-closed discipline as every other guess-scoring edge case here.
```

```csharp
  105|     private async Task<ScoreResult> AcceptMatchAsync(
  106|         GridCell cell, Guid instanceId, IReadOnlyList<Player> matching, Guid? chosenPlayerId, CancellationToken cancellationToken)
  107|     {
  108|         if (chosenPlayerId is not null)
  109|         {
  110|             var chosen = matching.FirstOrDefault(p => p.Id == chosenPlayerId.Value);
  111|             return chosen is null
  112|                 ? new ScoreResult { IsCorrect = false }
  113|                 : new ScoreResult { IsCorrect = true, PlayerAnswerId = chosen.Id };
  114|         }
  115| 
  116|         if (matching.Count == 0)
  117|             return new ScoreResult { IsCorrect = false };
  118| 
  119|         if (matching.Count == 1)
  120|             return new ScoreResult { IsCorrect = true, PlayerAnswerId = matching[0].Id };
  121| 
  122|         logger.LogInformation(
  123|             "Guess for cell {CellId} in instance {InstanceId} matched {Count} fitting candidates; " +
  124|             "showing a disambiguation prompt per REQ-209.",
  125|             cell.Id, instanceId, matching.Count);
  126| 
  127|         var candidates = await BuildDisambiguationCandidatesAsync(cell, matching, cancellationToken);
  128|         return new ScoreResult { IsCorrect = false, DisambiguationCandidates = candidates };
  129|     }
```

### Data flow

**`cell`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridNameMatcher.cs`:
```
   24|     // resolved to zero candidates satisfying both of the cell's categories.
   38|         GridCell cell, string normalizedName, Guid? chosenPlayerId, Guid instanceId, CancellationToken cancellationToken)
   41|         var matching = await FilterByCategoriesAsync(cell, exactCandidates, cancellationToken);
   50|             matching = await FilterByCategoriesAsync(cell, aliasCandidates, cancellationToken);
   59|             var fuzzyCandidates = await FindFuzzyCandidatesAsync(cell, normalizedName, cancellationToken);
   60|             matching = await FilterByCategoriesAsync(cell, fuzzyCandidates, cancellationToken);
   63|         return await AcceptMatchAsync(cell, instanceId, matching, chosenPlayerId, cancellationToken);
   67|     // stage: a candidate is only ever a real answer for this cell if it
   71|         GridCell cell, IReadOnlyList<Player> candidates, CancellationToken cancellationToken)
   77|                 candidate.Id, CategoryPairingRules.MapAttributeType(cell.RowCategoryType), cell.RowCategoryValue, cancellationToken);
   82|                 candidate.Id, CategoryPairingRules.MapAttributeType(cell.ColCategoryType), cell.ColCategoryValue, cancellationToken);
  106|         GridCell cell, Guid instanceId, IReadOnlyList<Player> matching, Guid? chosenPlayerId, CancellationToken cancellationToken)
  123|             "Guess for cell {CellId} in instance {InstanceId} matched {Count} fitting candidates; " +  <-- mutation site
  125|             cell.Id, instanceId, matching.Count);
  127|         var candidates = await BuildDisambiguationCandidatesAsync(cell, matching, cancellationToken);
  133|     // whichever of the cell's own two categories every candidate already
  138|         GridCell cell, IReadOnlyList<Player> matching, CancellationToken cancellationToken)
  140|         var rowAttributeType = CategoryPairingRules.MapAttributeType(cell.RowCategoryType);
  141|         var colAttributeType = CategoryPairingRules.MapAttributeType(cell.ColCategoryType);
  150|                 cell, rowAttributeType, colAttributeType, attributesByPlayerId, player.Id);
  159|     // cell's two own categories are excluded here.
  161|         GridCell cell, string rowAttributeType, string colAttributeType,
  168|             .Where(a => !(a.AttributeType == rowAttributeType && a.AttributeValue == cell.RowCategoryValue) &&
  169|                         !(a.AttributeType == colAttributeType && a.AttributeValue == cell.ColCategoryValue))
  177|     // least one of this cell's two categories — a player satisfying neither
  178|     // can never be a correct answer for this cell regardless of name, so
  180|     // bounded by this cell's own category population, never a full-table
  185|         GridCell cell, string normalizedName, CancellationToken cancellationToken)
  188|             CategoryPairingRules.MapAttributeType(cell.RowCategoryType), cell.RowCategoryValue,
  189|             CategoryPairingRules.MapAttributeType(cell.ColCategoryType), cell.ColCategoryValue,
  249|     // independently, an optional photo) for a cell that has just locked with
  272|         // resolving some OTHER cell's answer key, in which case no live
```

### Tests

no test matched by name (searched `backend/tests/XGArcade.Games.XGGrid.Tests/GridNameMatcherTests.cs` for tests referencing `AcceptMatchAsync`)

### REQ/ADR references and comments within the method

References found: REQ-209

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 277

**File:** `backend/src/XGArcade.Games.XGGrid/GridNameMatcher.cs`  
**Mutated line(s):** 168-168  
**Mutator:** Logical mutation

**Original:**
```csharp
            .Where(a => !(a.AttributeType == rowAttributeType && a.AttributeValue == cell.RowCategoryValue) &&
```
**Mutated replacement:**
```csharp
a.AttributeType == rowAttributeType || a.AttributeValue == cell.RowCategoryValue
```

### Containing method: `GetDistinguishingAttributeValues` (lines 160-173, 14 lines)

**Leading doc comment:**
```
    // The non-redundant half of a matching player's attributes —
    // BuildDisambiguationCandidatesAsync's own doc comment explains why the
    // cell's two own categories are excluded here.
```

```csharp
  160|     private static IReadOnlyList<string> GetDistinguishingAttributeValues(
  161|         GridCell cell, string rowAttributeType, string colAttributeType,
  162|         IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAttribute>> attributesByPlayerId, Guid playerId)
  163|     {
  164|         if (!attributesByPlayerId.TryGetValue(playerId, out var attributes))
  165|             return [];
  166| 
  167|         return attributes
  168|             .Where(a => !(a.AttributeType == rowAttributeType && a.AttributeValue == cell.RowCategoryValue) &&
  169|                         !(a.AttributeType == colAttributeType && a.AttributeValue == cell.ColCategoryValue))
  170|             .Select(a => a.AttributeValue)
  171|             .Distinct()
  172|             .ToList();
  173|     }
```

### Data flow

**`AttributeType`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridNameMatcher.cs`:
```
  168|             .Where(a => !(a.AttributeType == rowAttributeType && a.AttributeValue == cell.RowCategoryValue) &&  <-- mutation site
  169|                         !(a.AttributeType == colAttributeType && a.AttributeValue == cell.ColCategoryValue))
```

**`rowAttributeType`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/GridNameMatcher.cs`:
```
  140|         var rowAttributeType = CategoryPairingRules.MapAttributeType(cell.RowCategoryType);
  150|                 cell, rowAttributeType, colAttributeType, attributesByPlayerId, player.Id);
  161|         GridCell cell, string rowAttributeType, string colAttributeType,
  168|             .Where(a => !(a.AttributeType == rowAttributeType && a.AttributeValue == cell.RowCategoryValue) &&  <-- mutation site
```

### Tests

no test matched by name (searched `backend/tests/XGArcade.Games.XGGrid.Tests/GridNameMatcherTests.cs` for tests referencing `GetDistinguishingAttributeValues`)

### REQ/ADR references and comments within the method

No REQ-xxx/ADR-xxx references found within the method body or its doc comment.

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 362

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 188-189  
**Mutator:** Statement mutation

**Original:**
```csharp
                    logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
                        country.Name, club.Name, PersistentFailureThreshold);
```
**Mutated replacement:**
```csharp
;
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`logger`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
   96|     ILogger<PlayerCacheWarmingService> logger) : IPlayerCacheWarmingService
  138|         logger.LogInformation(
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",  <-- mutation site
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  312|         logger.LogInformation(
  333|             logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",
```

**`LogDebug`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",  <-- mutation site
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
```

**`Country`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
    9| // Country x Club and Club x Club pair, instead of only ever discovering a
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",  <-- mutation site
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  245|                 // REQ-110: see the Country x Club loop's own comment above
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  282|                         // REQ-110: see the Country x Club loop's own comment
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 388

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 228-228  
**Mutator:** Statement mutation

**Original:**
```csharp
                LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
```
**Mutated replacement:**
```csharp
;
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`LogProgressCheckpoint`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);  <-- mutation site
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
```

**`pairsProcessed`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  147|         var pairsProcessed = 0;
  156|                 pairsProcessed++;
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);  <-- mutation site
  237|                 pairsProcessed++;
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
```

**`totalPairs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);  <-- mutation site
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 413

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 251-252  
**Mutator:** Statement mutation

**Original:**
```csharp
                    logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
                        clubs[i].Name, clubs[j].Name);
```
**Mutated replacement:**
```csharp
;
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`logger`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
   96|     ILogger<PlayerCacheWarmingService> logger) : IPlayerCacheWarmingService
  138|         logger.LogInformation(
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",  <-- mutation site
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  312|         logger.LogInformation(
  333|             logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",
```

**`LogDebug`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",  <-- mutation site
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
```

**`ClubA`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",  <-- mutation site
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 422

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 268-268  
**Mutator:** Boolean mutation

**Original:**
```csharp
                    var hadTechnicalFailure = false;
```
**Mutated replacement:**
```csharp
true
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`hadTechnicalFailure`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  193|                     var hadTechnicalFailure = false;
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  198|                     if (hadTechnicalFailure)
  268|                     var hadTechnicalFailure = false;  <-- mutation site
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  273|                     if (hadTechnicalFailure)
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 430

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 276-276  
**Mutator:** Statement mutation

**Original:**
```csharp
                        failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
```
**Mutated replacement:**
```csharp
;
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`failingPairs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  149|         var failingPairs = new List<string>();
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");  <-- mutation site
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
```

**`Add`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");  <-- mutation site
```

**`clubs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  153|             foreach (var club in clubs)
  232|         for (var i = 0; i < clubs.Count; i++)
  234|             for (var j = i + 1; j < clubs.Count; j++)
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  252|                         clubs[i].Name, clubs[j].Name);
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");  <-- mutation site
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 431

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 276-276  
**Mutator:** String mutation

**Original:**
```csharp
                        failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
```
**Mutated replacement:**
```csharp
$""
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`failingPairs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  149|         var failingPairs = new List<string>();
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");  <-- mutation site
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
```

**`Add`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");  <-- mutation site
```

**`clubs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  153|             foreach (var club in clubs)
  232|         for (var i = 0; i < clubs.Count; i++)
  234|             for (var j = i + 1; j < clubs.Count; j++)
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  252|                         clubs[i].Name, clubs[j].Name);
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");  <-- mutation site
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 451

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 319-319  
**Mutator:** Conditional (true) mutation

**Original:**
```csharp
            result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
```
**Mutated replacement:**
```csharp
(true?$" Failing pairs: {string.Join(", ", result.FailingPairs)}." :string.Empty)
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`result`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  213|                         // result set (implementation-document.md §6a), so
  300|         var result = new CacheWarmingResult(
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
  321|         return result;
```

**`FailingPairs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
   58| // FailingPairs below.
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
```

**`Count`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  212|                         // matches.Count is the query's complete, un-LIMITed
  218|                         if (matches.Count < options.MinValidAnswers)
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  232|         for (var i = 0; i < clubs.Count; i++)
  234|             for (var j = i + 1; j < clubs.Count; j++)
  286|                         if (matches.Count < options.MinValidAnswers)
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 457

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 319-319  
**Mutator:** String mutation

**Original:**
```csharp
            result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
```
**Mutated replacement:**
```csharp
""
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`result`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  213|                         // result set (implementation-document.md §6a), so
  300|         var result = new CacheWarmingResult(
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
  321|         return result;
```

**`FailingPairs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
   58| // FailingPairs below.
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
```

**`Count`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  212|                         // matches.Count is the query's complete, un-LIMITed
  218|                         if (matches.Count < options.MinValidAnswers)
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  232|         for (var i = 0; i < clubs.Count; i++)
  234|             for (var j = i + 1; j < clubs.Count; j++)
  286|                         if (matches.Count < options.MinValidAnswers)
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 458

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 319-319  
**Mutator:** String mutation

**Original:**
```csharp
            result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
```
**Mutated replacement:**
```csharp
"Stryker was here!"
```

### Containing method: `WarmAsync` (lines 129-322, 194 lines)
*(exceeds 150 lines — included in full anyway, per instructions)*

```csharp
  129|     public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
  130|     {
  131|         var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
  132|         var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
  133| 
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  137| 
  138|         logger.LogInformation(
  139|             "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
  140|             "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  142| 
  143|         var pairsQueriedLive = 0;
  144|         var pairsAlreadyValid = 0;
  145|         var pairsSkippedConfirmedLow = 0;
  146|         var pairsSkippedPersistentFailure = 0;
  147|         var pairsProcessed = 0;
  148|         var pairsWithTechnicalFailure = 0;
  149|         var failingPairs = new List<string>();
  150| 
  151|         foreach (var country in countries)
  152|         {
  153|             foreach (var club in clubs)
  154|             {
  155|                 cancellationToken.ThrowIfCancellationRequested();
  156|                 pairsProcessed++;
  157| 
  158|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  159|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  160|                 if (cachedCount >= options.MinValidAnswers)
  161|                 {
  162|                     pairsAlreadyValid++;
  163|                 }
  164|                 // REQ-110 (2026-07-28 "persisted confirmed-low signal"
  165|                 // extension): checked only once cachedCount has already
  166|                 // shown this pair is below threshold THIS run (a real,
  167|                 // freshly-computed count, not a stale one) — so this check
  168|                 // is safe even if MinValidAnswers itself has changed since
  169|                 // the pair was marked (see ConfirmedLowMatchPair's own doc
  170|                 // comment for why that ordering matters).
  171|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  172|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
  173|                 {
  174|                     pairsSkippedConfirmedLow++;
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  176|                         country.Name, club.Name);
  177|                 }
  178|                 // REQ-110 (2026-08-01 "persistent technical-failure
  179|                 // tracking" extension): checked only once the pair is
  180|                 // neither already-valid nor confirmed-low — see
  181|                 // PairLookupFailure's own doc comment and
  182|                 // PersistentFailureThreshold's own comment for the full
  183|                 // "why 2 consecutive runs, not 1" reasoning.
  184|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  185|                     NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
  186|                 {
  187|                     pairsSkippedPersistentFailure++;
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  189|                         country.Name, club.Name, PersistentFailureThreshold);
  190|                 }
  191|                 else
  192|                 {
  193|                     var hadTechnicalFailure = false;
  194|                     var matches = await wikidataLookupService.LookupAndPersistAsync(
  195|                         country, club, WikidataLookupOrigin.Sync, cancellationToken,
  196|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  197|                     pairsQueriedLive++;
  198|                     if (hadTechnicalFailure)
  199|                     {
  200|                         pairsWithTechnicalFailure++;
  201|                         failingPairs.Add($"{country.Name} x {club.Name}");
  202|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  203|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  204|                     }
  205|                     else
  206|                     {
  207|                         // REQ-110: a real (possibly zero-match) answer — not
  208|                         // a swallowed technical failure — so clear any prior
  209|                         // run's failure marker (a no-op if this pair never
  210|                         // failed before) and, if it's still below threshold,
  211|                         // persist the confirmed-low marker for next run.
  212|                         // matches.Count is the query's complete, un-LIMITed
  213|                         // result set (implementation-document.md §6a), so
  214|                         // it's the true current match count, not just
  215|                         // "however many were new."
  216|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  217|                             NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
  218|                         if (matches.Count < options.MinValidAnswers)
  219|                         {
  220|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  222|                         }
  223|                     }
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  226|                 }
  227| 
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  229|             }
  230|         }
  231| 
  232|         for (var i = 0; i < clubs.Count; i++)
  233|         {
  234|             for (var j = i + 1; j < clubs.Count; j++)
  235|             {
  236|                 cancellationToken.ThrowIfCancellationRequested();
  237|                 pairsProcessed++;
  238| 
  239|                 var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
  240|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  241|                 if (cachedCount >= options.MinValidAnswers)
  242|                 {
  243|                     pairsAlreadyValid++;
  244|                 }
  245|                 // REQ-110: see the Country x Club loop's own comment above
  246|                 // — same reasoning here.
  247|                 else if (await playerDataQualityRepository.IsConfirmedLowAsync(
  248|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
  249|                 {
  250|                     pairsSkippedConfirmedLow++;
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  252|                         clubs[i].Name, clubs[j].Name);
  253|                 }
  254|                 // REQ-110 (2026-08-01): see the Country x Club loop's own
  255|                 // comment above — same reasoning here. This is the loop
  256|                 // that actually needed this extension in practice — see
  257|                 // WikidataClient.BuildClubClubIntersectionQuery's own
  258|                 // comment for the specific club-club query-shape incident.
  259|                 else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
  260|                     ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
  261|                 {
  262|                     pairsSkippedPersistentFailure++;
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  264|                         clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
  265|                 }
  266|                 else
  267|                 {
  268|                     var hadTechnicalFailure = false;
  269|                     var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
  270|                         clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
  271|                         onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
  272|                     pairsQueriedLive++;
  273|                     if (hadTechnicalFailure)
  274|                     {
  275|                         pairsWithTechnicalFailure++;
  276|                         failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
  277|                         await playerDataQualityRepository.RecordTechnicalFailureAsync(
  278|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  279|                     }
  280|                     else
  281|                     {
  282|                         // REQ-110: see the Country x Club loop's own comment
  283|                         // above — same reasoning here.
  284|                         await playerDataQualityRepository.ClearTechnicalFailureAsync(
  285|                             ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
  286|                         if (matches.Count < options.MinValidAnswers)
  287|                         {
  288|                             await playerDataQualityRepository.RecordConfirmedLowAsync(
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  290|                         }
  291|                     }
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  294|                 }
  295| 
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  297|             }
  298|         }
  299| 
  300|         var result = new CacheWarmingResult(
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  302|             pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);
  303| 
  304|         // REQ-110: the failing-pairs list is logged in full here, at
  305|         // Information level, exactly once per run — not per-pair (each
  306|         // pair's own failure was already logged inside WikidataClient when
  307|         // it happened, at Debug level as of 2026-08-01 — see
  308|         // RunIntersectionQueryAsync's own comment on why). A comma-joined
  309|         // string rather than one log call per pair, matching this method's
  310|         // existing "coarse summary, not a per-pair stream" logging shape
  311|         // (see ProgressLogInterval's own comment).
  312|         logger.LogInformation(
  313|             "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
  314|             "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
  315|             "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
  316|             "rather than a clean answer.{FailingPairsSuffix}",
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);
  320| 
  321|         return result;
  322|     }
```

### Data flow

**`result`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  213|                         // result set (implementation-document.md §6a), so
  300|         var result = new CacheWarmingResult(
  317|             result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
  318|             result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
  321|         return result;
```

**`FailingPairs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
   58| // FailingPairs below.
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
```

**`Count`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  134|         var countryClubPairCount = countries.Count * clubs.Count;
  135|         var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  212|                         // matches.Count is the query's complete, un-LIMITed
  218|                         if (matches.Count < options.MinValidAnswers)
  221|                                 NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
  225|                         country.Name, club.Name, matches.Count, cachedCount);
  232|         for (var i = 0; i < clubs.Count; i++)
  234|             for (var j = i + 1; j < clubs.Count; j++)
  286|                         if (matches.Count < options.MinValidAnswers)
  289|                                 ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
  293|                         clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
  319|             result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);  <-- mutation site
```

### Tests

**`REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCachedData_QueriesEveryCountryClubAndClubClubPairLive()
    {
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 2 countries x 2 clubs = 4 Country x Club pairs, plus 1 unique
        // Club x Club pair (Arsenal x Barcelona) = 5 total.
        Assert.That(result.TotalPairs, Is.EqualTo(5));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(5));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Barcelona"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("Spain", "Barcelona"), Is.EqualTo(1));
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the pair could have been queried as (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — summing both possible key orders instead
        // of asserting a specific one, same defensive technique as
        // GridGameModuleTests.cs's shuffle-order-independent assertions.
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairAlreadyAtOrAboveMinValidAnswers_SkipsLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 5);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsAlreadyValid, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already meeting MinValidAnswers must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_BelowThresholdPairNotYetConfirmedLow_IsQueriedLiveNotSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("nationality", "France", "club", "Arsenal", count: 2);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "the skip-if-confirmed-low path must not trigger for a pair no prior run has confirmed low");
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
    }
```

**`REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailureOnLiveQuery_CountedSeparatelyFromSuccessfulZeroMatchResponse()
    {
        // A single shared club (Arsenal) — a single club has zero Club x
        // Club pairs to pair with itself, so this seeds exactly 2 Country x
        // Club pairs (France x Arsenal, Spain x Arsenal) and nothing else,
        // keeping this test's pair count exact and easy to reason about.
        SeedCountry("France");
        SeedCountry("Spain");
        SeedClub("Arsenal");
        // France x Arsenal: the run's one attempt hits a technical failure
        // (no same-run retry as of the 2026-08-01 extension — see
        // REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry).
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        // Spain x Arsenal: no failure configured, no matches configured —
        // a genuine "queried successfully, found nothing" response.
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(2), "PairsQueriedLive counts every live-queried pair, technical failure or not");
        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Is.EquivalentTo(new[] { "France x Arsenal" }));
        Assert.That(result.FailingPairs, Does.Not.Contain("Spain x Arsenal"),
            "a genuine zero-match success must never be listed as a failing pair");
    }
```

**`REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_TechnicalFailure_MakesExactlyOneLiveCall_NoSameRunRetry()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailure("France", "Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(result.FailingPairs, Has.Count.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1),
            "the same-run retry was removed 2026-08-01 (ADR-0052) — a failing pair costs exactly one live call, not two");
    }
```

**`REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SinglePriorRunFailure_StillQueriedLiveNotYetSkipped()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        // First run: fails once. Second run: no failure configured, and a
        // real match set — proves the pair is still queried live, not
        // skipped, after only one prior failure.
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsSkippedPersistentFailure, Is.EqualTo(0),
            "one prior run's failure must not be enough to skip — PersistentFailureThreshold is 2 consecutive runs");
        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "both runs' single attempts must actually have been made");
    }
```

**`REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        var secondRun = await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(2),
            "the third run must not issue a live call at all once the pair is skipped as a persistent failure");
    }
```

**`REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPairFailsTwoConsecutiveRuns_SkippedWithoutLiveQueryOnThirdRun()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Arsenal", "Barcelona", attempts: 2);
        _wikidataLookupService.FailClubClubWithTechnicalFailureForAttempts("Barcelona", "Arsenal", attempts: 2);
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();
        await service.WarmAsync();
        var thirdRun = await service.WarmAsync();

        Assert.That(thirdRun.PairsSkippedPersistentFailure, Is.EqualTo(1));
        Assert.That(thirdRun.PairsQueriedLive, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PairRecoversAfterFailure_ClearsPersistedFailureMarker()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.FailWithTechnicalFailureForAttempts("France", "Arsenal", attempts: 1);
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var firstRun = await service.WarmAsync();
        Assert.That(firstRun.PairsWithTechnicalFailure, Is.EqualTo(1));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.True,
            "one technical failure must already be recorded before the recovery run");

        var secondRun = await service.WarmAsync();

        Assert.That(secondRun.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(secondRun.PairsWithTechnicalFailure, Is.EqualTo(0));
        Assert.That(await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1), Is.False,
            "the marker must be cleared once the pair gets a real answer, even a below-threshold one — otherwise a pair that recovers would still count toward a future skip");
    }
```

**`REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_RealBelowThresholdAnswer_PersistsConfirmedLowMarker_ButNotSkippedThisRun()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        _wikidataLookupService.SetMatches("France", "Arsenal", BuildFakePlayers("France", "Arsenal", count: 2));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0),
            "a pair confirmed low FOR THE FIRST TIME this run is still counted as queried live, not skipped, this run");
        Assert.That(await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.True,
            "the confirmed-low marker must be persisted so a LATER run can skip it");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowPair_SkippedWithoutLiveQuery()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_PreviouslyConfirmedLowClubClubPair_SkippedWithoutLiveQuery()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        // GetClubsAsync has no explicit ordering (CategoryValueRepository),
        // so the loop could check either (Arsenal, Barcelona) or
        // (Barcelona, Arsenal) — seeding both orders so this test doesn't
        // depend on which one the loop actually uses (same defensive
        // technique as this file's other order-independent assertions).
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Arsenal", "club", "Barcelona", matchCount: 0);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("club", "Barcelona", "club", "Arsenal", matchCount: 0);
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(1));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(
            _wikidataLookupService.GetClubClubCallCount("Arsenal", "Barcelona") + _wikidataLookupService.GetClubClubCallCount("Barcelona", "Arsenal"),
            Is.EqualTo(0),
            "a Club x Club pair already confirmed low by a prior run must never trigger a live Wikidata call");
    }
```

**`REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_CountryClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_ClubClubPair_PassesCacheWarmingTimeoutTierToLookupService()
    {
        SeedClub("Arsenal");
        SeedClub("Barcelona");
        var service = BuildService(minValidAnswers: 5);

        await service.WarmAsync();

        var tier = _wikidataLookupService.GetClubClubLastTimeoutTier("Arsenal", "Barcelona")
            ?? _wikidataLookupService.GetClubClubLastTimeoutTier("Barcelona", "Arsenal");
        Assert.That(tier, Is.EqualTo(WikidataQueryTimeoutTier.CacheWarming));
    }
```

**`REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_NoCountriesOrClubs_ReturnsZeroTotalPairs()
    {
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(result.TotalPairs, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(0));
        Assert.That(result.PairsAlreadyValid, Is.EqualTo(0));
    }
```

**`REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_WarmAsync_SingleClub_HasNoClubClubPairsToWarm()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        // 1 country x 1 club = 1 Country x Club pair; C(1,2) = 0 Club x Club
        // pairs — a single club can never pair with itself.
        Assert.That(result.TotalPairs, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        // Seed the state a prior WarmAsync run would have left behind: a
        // real confirmed-low marker for this exact pair, mirroring
        // PlayerCacheWarmingService's own nationality-then-club ordering.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 1);

        // REQ-111 named-mode cleanup — e.g. after Napoli's WikidataQid was
        // corrected — must clear the stale marker alongside the stale
        // PlayerAttribute/PlayerData rows (already covered in isolation by
        // StaleClubAttributeCleanerTests.cs's own REQ110_CleanAsync_
        // RemovesConfirmedLowMatchPair_OnACountryClubPairsClubSide).
        await StaleClubAttributeCleaner.CleanAsync(_dbContext, ["Napoli"]);

        // Configure the fake so a live query for this pair would actually
        // return a real answer if (and only if) WarmAsync issues one.
        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "cleaning the stale confirmed-low marker must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_StaleClubAttributeCleanerCleanAllSeededClubsAsync_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        SeedCountry("France");
        var napoli = SeedClub("Napoli");
        // CleanAllSeededClubsAsync resolves its club-name list from
        // ClubDefinition — SeedClub above already adds the ClubDefinition
        // row, so no separate seeding step is needed here (unlike
        // StaleClubAttributeCleanerTests.cs's SeedClubDefinitionAsync helper,
        // which exists there only because that file's other tests seed
        // players without a matching ClubDefinition row at all).
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Id == napoli.Id), Is.True);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        // REQ-111 --all-clubs mode — e.g. after the 2026-07-17 truthy-wdt:P54
        // incident tainted every seeded club's cached data at once — must
        // also clear the stale marker (isolation coverage:
        // StaleClubAttributeCleanerTests.cs's REQ110_CleanAllSeededClubsAsync_
        // RemovesConfirmedLowMatchPairsForEverySeededClub).
        await StaleClubAttributeCleaner.CleanAllSeededClubsAsync(_dbContext);

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "--all-clubs cleanup must make WarmAsync re-query this pair live, not trust the marker left over from before the cleanup");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolConfirmedLowMatchPairsDelete_ThenWarmAsync_ReQueriesPreviouslyConfirmedLowPairLive()
    {
        // purge-player-pool is a CLI verb inside Program.cs's top-level
        // statements (Npgsql-only — it builds its own DbContextOptions with
        // .UseNpgsql(...) and requires the exact confirmation-phrase
        // argument), so it can't be invoked directly from a unit test. Its
        // ConfirmedLowMatchPair-clearing logic itself is exactly one line:
        //   await purgeDbContext.ConfirmedLowMatchPairs.ExecuteDeleteAsync();
        // (see Program.cs's `if (args is ["purge-player-pool", ..])` block).
        // ExecuteDeleteAsync is a relational-provider bulk operation not
        // supported by the InMemory provider this test (and this whole file)
        // uses, so RemoveRange + SaveChangesAsync below is used as a faithful
        // proxy for it — both leave the table in the exact same end state
        // (zero ConfirmedLowMatchPair rows), which is the only thing this
        // regression test or WarmAsync's downstream behavior can observe.
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Napoli", matchCount: 0);

        var staleConfirmedLow = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        _dbContext.ConfirmedLowMatchPairs.RemoveRange(staleConfirmedLow);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped ConfirmedLowMatchPair delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedConfirmedLow, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

**`REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive`** (from `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs`):
```csharp
[Test]
    public async Task REQ110_PurgePlayerPoolPairLookupFailuresDelete_ThenWarmAsync_ReQueriesPreviouslyFailingPairLive()
    {
        SeedCountry("France");
        SeedClub("Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Napoli");

        var staleLookupFailures = await _dbContext.PairLookupFailures.ToListAsync();
        _dbContext.PairLookupFailures.RemoveRange(staleLookupFailures);
        await _dbContext.SaveChangesAsync();

        _wikidataLookupService.SetMatches("France", "Napoli", BuildFakePlayers("France", "Napoli", count: 7));
        var service = BuildService(minValidAnswers: 5);

        var result = await service.WarmAsync();

        Assert.That(_wikidataLookupService.GetCallCount("France", "Napoli"), Is.EqualTo(1),
            "purge-player-pool's unscoped PairLookupFailure delete must make WarmAsync re-query this pair live, not trust a marker left over from before the purge");
        Assert.That(result.PairsSkippedPersistentFailure, Is.EqualTo(0));
        Assert.That(result.PairsQueriedLive, Is.EqualTo(1));
    }
```

### REQ/ADR references and comments within the method

References found: REQ-110

Inline comments within the method:
```
// REQ-110 (2026-07-28 "persisted confirmed-low signal"
// extension): checked only once cachedCount has already
// shown this pair is below threshold THIS run (a real,
// freshly-computed count, not a stale one) — so this check
// is safe even if MinValidAnswers itself has changed since
// the pair was marked (see ConfirmedLowMatchPair's own doc
// comment for why that ordering matters).
// REQ-110 (2026-08-01 "persistent technical-failure
// tracking" extension): checked only once the pair is
// neither already-valid nor confirmed-low — see
// PairLookupFailure's own doc comment and
// PersistentFailureThreshold's own comment for the full
// "why 2 consecutive runs, not 1" reasoning.
// REQ-110: a real (possibly zero-match) answer — not
// a swallowed technical failure — so clear any prior
// run's failure marker (a no-op if this pair never
// failed before) and, if it's still below threshold,
// persist the confirmed-low marker for next run.
// matches.Count is the query's complete, un-LIMITed
// result set (implementation-document.md §6a), so
// it's the true current match count, not just
// "however many were new."
// REQ-110: see the Country x Club loop's own comment above
// — same reasoning here.
// REQ-110 (2026-08-01): see the Country x Club loop's own
// comment above — same reasoning here. This is the loop
// that actually needed this extension in practice — see
// WikidataClient.BuildClubClubIntersectionQuery's own
// comment for the specific club-club query-shape incident.
// REQ-110: see the Country x Club loop's own comment
// above — same reasoning here.
// REQ-110: the failing-pairs list is logged in full here, at
// Information level, exactly once per run — not per-pair (each
// pair's own failure was already logged inside WikidataClient when
// it happened, at Debug level as of 2026-08-01 — see
// RunIntersectionQueryAsync's own comment on why). A comma-joined
// string rather than one log call per pair, matching this method's
// existing "coarse summary, not a per-pair stream" logging shape
// (see ProgressLogInterval's own comment).
```

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 460

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 332-332  
**Mutator:** Logical mutation

**Original:**
```csharp
        if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
```
**Mutated replacement:**
```csharp
pairsProcessed % ProgressLogInterval == 0 && pairsProcessed == totalPairs
```

### Containing method: `LogProgressCheckpoint` (lines 330-335, 6 lines)

**Leading doc comment:**
```
    // REQ-110 (2026-08-01): includes the running technical-failure count so
    // a run that gets cancelled mid-way (this job's own 90-minute CI
    // timeout, or a manual cancellation) still leaves a useful trail in the
    // log — WarmAsync's own Information-level summary line never gets to
    // run if the process is killed first, so this periodic checkpoint is
    // the only signal an operator gets from an incomplete run.
```

```csharp
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  331|     {
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
  333|             logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  335|     }
```

### Data flow

**`pairsProcessed`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  147|         var pairsProcessed = 0;
  156|                 pairsProcessed++;
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  237|                 pairsProcessed++;
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)  <-- mutation site
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
```

**`ProgressLogInterval`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  104|     private const int ProgressLogInterval = 25;
  311|         // (see ProgressLogInterval's own comment).
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)  <-- mutation site
```

**`totalPairs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)  <-- mutation site
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
```

### Tests

no test matched by name (searched `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs` for tests referencing `LogProgressCheckpoint`)

### REQ/ADR references and comments within the method

No REQ-xxx/ADR-xxx references found within the method body or its doc comment.

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 464

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 332-332  
**Mutator:** Equality mutation

**Original:**
```csharp
        if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
```
**Mutated replacement:**
```csharp
pairsProcessed != totalPairs
```

### Containing method: `LogProgressCheckpoint` (lines 330-335, 6 lines)

**Leading doc comment:**
```
    // REQ-110 (2026-08-01): includes the running technical-failure count so
    // a run that gets cancelled mid-way (this job's own 90-minute CI
    // timeout, or a manual cancellation) still leaves a useful trail in the
    // log — WarmAsync's own Information-level summary line never gets to
    // run if the process is killed first, so this periodic checkpoint is
    // the only signal an operator gets from an incomplete run.
```

```csharp
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  331|     {
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
  333|             logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  335|     }
```

### Data flow

**`pairsProcessed`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  147|         var pairsProcessed = 0;
  156|                 pairsProcessed++;
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  237|                 pairsProcessed++;
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)  <-- mutation site
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
```

**`ProgressLogInterval`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  104|     private const int ProgressLogInterval = 25;
  311|         // (see ProgressLogInterval's own comment).
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)  <-- mutation site
```

**`totalPairs`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  136|         var totalPairs = countryClubPairCount + clubClubPairCount;
  141|             countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);
  228|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  296|                 LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  301|             totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)  <-- mutation site
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
```

### Tests

no test matched by name (searched `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs` for tests referencing `LogProgressCheckpoint`)

### REQ/ADR references and comments within the method

No REQ-xxx/ADR-xxx references found within the method body or its doc comment.

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

## Mutant 466

**File:** `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`  
**Mutated line(s):** 333-333  
**Mutator:** String mutation

**Original:**
```csharp
            logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",
```
**Mutated replacement:**
```csharp
""
```

### Containing method: `LogProgressCheckpoint` (lines 330-335, 6 lines)

**Leading doc comment:**
```
    // REQ-110 (2026-08-01): includes the running technical-failure count so
    // a run that gets cancelled mid-way (this job's own 90-minute CI
    // timeout, or a manual cancellation) still leaves a useful trail in the
    // log — WarmAsync's own Information-level summary line never gets to
    // run if the process is killed first, so this periodic checkpoint is
    // the only signal an operator gets from an incomplete run.
```

```csharp
  330|     private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
  331|     {
  332|         if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
  333|             logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",
  334|                 pairsProcessed, totalPairs, pairsWithTechnicalFailure);
  335|     }
```

### Data flow

**`logger`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
   96|     ILogger<PlayerCacheWarmingService> logger) : IPlayerCacheWarmingService
  138|         logger.LogInformation(
  175|                     logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
  188|                     logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  224|                     logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
  251|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
  263|                     logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
  292|                     logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
  312|         logger.LogInformation(
  333|             logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",  <-- mutation site
```

**`LogInformation`** — every occurrence in `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs`:
```
  138|         logger.LogInformation(
  312|         logger.LogInformation(
  333|             logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",  <-- mutation site
```

### Tests

no test matched by name (searched `backend/tests/XGArcade.Games.XGGrid.Tests/PlayerCacheWarmingServiceTests.cs` for tests referencing `LogProgressCheckpoint`)

### REQ/ADR references and comments within the method

No REQ-xxx/ADR-xxx references found within the method body or its doc comment.

---
- Classification: [ real_gap | equivalent | noise ]
- Severity (real_gap only): [ high | medium | low ]
- Reason:
- Confidence: [ certain | unsure ]

---

