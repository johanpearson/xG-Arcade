Subject: Confirming intended use case — fantasy/gameplay product with local caching

Hi API-Football team,

I'm building xG Arcade, a football trivia/guessing game (players combine
two categories — e.g. club × country — to guess a matching professional
footballer, similar in spirit to a fantasy football game). I'd like to
confirm my planned use of your API is within your terms before I rely on
it further.

Planned usage:
- Fetching player data (nationality, club history, trophies) to validate
  player guesses against
- Caching fetched data locally and permanently, rather than re-querying
  your API repeatedly for the same information
- No resale or redistribution of your raw data — it's used only to power
  gameplay logic within the app itself
- Possibly fetching club crest images in a future phase, which I saw your
  own documentation recommends caching locally rather than re-fetching

I noticed your terms mention fantasy sports games as an intended use case
for the data, which matches what I'm building. One clause I want to
double-check: the line stating you don't provide a "license for the use
and publication of the data... on applications, websites, or any other
products made by the user." Could you confirm whether the use case
described above (a gameplay product, not a data resale or competing data
product) is acceptable under your terms?

Happy to answer any questions about the project. Thanks for your time.

Best,
Johan
