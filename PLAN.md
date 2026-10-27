# TestGen — plan

Aplikacja konsolowa .NET, która uzupełnia brakujące testy jednostkowe w solucji .NET z pomocą lokalnego LLM.

**Użycie:** `testgen` — bez parametrów CLI. Wszystkie ustawienia (w tym ścieżka do solucji) są w `appsettings.json`.

## Założenia

- Wejście: ścieżka do pliku `.sln` w `appsettings.json` (`TestGen:SolutionPath`).
- Roslyn (`MSBuildWorkspace`) do analizy i modyfikacji kodu testów.
- Własna kolejka w procesie (`System.Threading.Channels`), równoległa praca workerów.
- Każdy worker pracuje w osobnym `git worktree`.
- Jeden lokalny dostawca LLM (endpoint zgodny z OpenAI: Ollama, LM Studio, llama.cpp).
- Wynik: jeden branch zbiorczy `testgen/result`.
- Framework testowy, biblioteka mocków, biblioteka asercji i konwencje są wykrywane automatycznie z istniejących testów.
- Zmiany, które nie przejdą `dotnet build` i `dotnet test`, są cofane (worktree i branch usuwane).

## Przepływ

1. **Preflight**
   - Repo git z czystym drzewem.
   - Baseline `dotnet build` i `dotnet test`. Czerwony baseline = koniec pracy.
   - Walidacja konfiguracji LLM (`BaseUrl`, `Model`).

2. **Raport pokrycia (przed)**
   - `dotnet-coverage collect "dotnet test"`, format cobertura (bez dodawania `coverlet` do csproj).
   - Parser: klasa → metoda → niepokryte linie i gałęzie.
   - Zapis: `.testgen/coverage-before.json` i raport HTML.

3. **Bootstrap projektów testowych**
   - Dla każdego projektu produkcyjnego bez testów: utworzenie `<Nazwa>.Tests` z `ProjectReference`, dodanie do `.sln`, `dotnet restore`, commit na `testgen/result`.
   - Dobór bibliotek: najpierw to, co już używane w solucji; gdy brak testów w ogóle — domyślnie xUnit + NSubstitute + Shouldly (`TestGen:DefaultStack` w konfiguracji).
   - Robione przed kolejką, żeby workerzy nie tworzyli jednocześnie tego samego csproj. Worktree'e odgałęziają się od commita po bootstrapie.
   - Wymaga dostępu do NuGet lub offline cache.

4. **Analiza Roslyn**
   - Mapowanie wpisów cobertury na `INamedTypeSymbol` i węzły składni metod.
   - Szukanie istniejących testów: po nazwie (`XTests`, `XTest`), a gdy to nie wystarcza, semantycznie przez `SymbolFinder.FindReferencesAsync` w projektach testowych.
   - Wykrywanie konwencji: framework, mocki, asercje, AutoFixture, wzorzec nazw metod, komentarze AAA, setup (konstruktor / `SetUp` / fixture / buildery), kolejność usingów.
   - Zbieranie sygnatur zależności (parametry konstruktora, interfejsy) pod mocki.
   - Metody prywatne i internal: pokrywane tylko przez publiczne API. Ścieżki od publicznych metod do niepokrytych linii wyliczane z grafu wywołań (`SymbolFinder.FindCallersAsync`) i podawane modelowi jako wskazówka.

5. **Kolejka**
   - Jednostka pracy = docelowy plik testowy. Klasy trafiające do tego samego pliku idą jako jeden item (unika konfliktów przy scalaniu).
   - Priorytet: najwięcej niepokrytych linii.
   - Stan w `.testgen/state.json` (pending / running / done / failed / skipped, liczba prób) — przerwany run można wznowić.
   - Domyślnie `N = min(rdzenie/2, 4)`.

6. **Worker (jeden item)**
   - `git worktree add .testgen/wt/<id> -b testgen/<id> HEAD`.
   - Prompt: kod klasy, niepokryte metody z zakresami linii, cały istniejący plik testów, podsumowanie konwencji. Kontekst przycinany do `MaxPromptTokens` (najpierw ciała nieistotnych metod, potem sygnatury zależności).
   - LLM zwraca czysty kod C# w jednym bloku — **tylko nowe metody testowe** (plus brakujące usingi i pola). Parsuje i waliduje Roslyn.
   - Wstawienie do istniejącej klasy testowej: `DocumentEditor` + `Formatter`, bez przepisywania pliku.
   - Brak pliku testów: nowy plik w projekcie testowym, struktura folderów i konwencja odwzorowane z sąsiednich testów.
   - Zależności zewnętrzne (DB, HTTP, system plików): mockowane wykrytą biblioteką; gdy się nie da — item `skipped` z powodem w raporcie.
   - Pre-check: błędy kompilacji z `Compilation.GetDiagnostics()` przed `dotnet build`.
   - Weryfikacja tylko dotkniętego projektu testowego: `dotnet build`, potem `dotnet test`.
   - Porażka: do `MaxRetries` razy błędy wracają do LLM (pętla naprawcza); dalej czerwono → cofnięcie (`worktree remove --force` + `branch -D`).
   - Sukces: ponowny pomiar pokrycia klasy (brak wzrostu = odrzucenie), commit na `testgen/<id>`.

7. **Integracja i raport (po)**
   - Jeden wątek scalający łączy udane brancze po kolei (cherry-pick lub merge) do `testgen/result`.
   - Pełny `dotnet build` i `dotnet test` całego solution oraz raport pokrycia „po".
   - Podsumowanie: pokrycie przed/po, status każdej klasy, liczba tokenów.

## Bezpieczniki jakości

- Diff może dotykać wyłącznie projektów testowych, nigdy kodu produkcyjnego.
- Zakaz `[Skip]`, `[Fact(Skip=…)]`, `Assert.True(true)` i testów bez asercji.
- Zakaz usuwania lub modyfikowania istniejących testów — tylko dopisywanie.
- Timeout na `dotnet test`.
- Opcjonalnie: dwukrotne uruchomienie nowych testów w celu wykrycia niestabilnych.

## Konfiguracja (`appsettings.json` w `TestGen.Cli`)

Brak parametrów CLI — jedynym źródłem ustawień jest `appsettings.json` (plus standardowe zmienne środowiskowe .NET, np. `Llm__Model=...`, bo robi to domyślny `ConfigurationBuilder`).

```json
{
  "Llm": {
    "BaseUrl": "http://localhost:11434/v1/",
    "Model": "qwen2.5-coder:14b",
    "ApiKey": "",
    "TimeoutSeconds": 300,
    "Temperature": 0.2,
    "MaxOutputTokens": 4096,
    "MaxPromptTokens": 8000,
    "SystemPrompt": "Jesteś ekspertem od testów jednostkowych w C#. Zwracasz wyłącznie kod C# w jednym bloku.",
    "Headers": {}
  },
  "TestGen": {
    "SolutionPath": "/sciezka/do/solucji.sln",
    "Parallel": 3,
    "MaxRetries": 3,
    "MinCoverage": 80,
    "DefaultStack": { "Framework": "xunit", "Mocking": "NSubstitute", "Assertions": "Shouldly" }
  }
}
```

- Wzorzec ze skilla `dotnet-llm-http-config`: własna sekcja, nazwany `HttpClient` (`AddHttpClient("Llm")`), `LlmOptions` bindowane z sekcji `Llm`.
- Walidacja przy starcie: `SolutionPath` (istniejący plik `.sln`), `BaseUrl` i `Model` wymagane, inaczej czytelny błąd przed pomiarem pokrycia.
- `ApiKey` opcjonalny; pusty = brak nagłówka `Authorization`.
- `appsettings.json` leży obok binarki, nie w analizowanym solution.

## Wykluczenia z pokrycia (domyślne)

`[ExcludeFromCodeCoverage]`, `*.g.cs`, `Program`, migracje, DTO bez logiki. Cel pokrycia: bez progu globalnego — kolejka idzie od największych luk, `MinCoverage` zatrzymuje pracę nad pojedynczą klasą.

## Stack

- `Microsoft.Extensions.Hosting` / `Configuration`, `Spectre.Console`
- `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `Microsoft.Build.Locator`
- git przez `Process` (`LibGit2Sharp` słabo obsługuje worktree)
- `dotnet-coverage`

## Struktura solucji

```
TestGen.sln
 ├─ TestGen.Cli        (Generic Host, Spectre.Console, orkiestracja, appsettings.json)
 ├─ TestGen.Coverage   (dotnet-coverage runner, parser cobertury, raport HTML/JSON)
 ├─ TestGen.Analysis   (Roslyn: mapowanie, wykrywanie konwencji, wstawianie testów)
 ├─ TestGen.Git        (git worktree, branch, commit, merge przez Process)
 ├─ TestGen.Queue      (Channel, workerzy, stan JSON, wznawianie)
 ├─ TestGen.Llm        (ILlmClient + klient OpenAI-compatible, budowanie promptów)
 └─ TestGen.Tests
```

## Kolejność implementacji

1. `Git` i `Queue` — szkielet z workerami tworzącymi i usuwającymi worktree, plus `appsettings.json` i walidacja konfiguracji.
2. `Coverage` — uruchomienie, parser, raport „przed".
3. `Analysis` — mapowanie klasa → plik → testy, wykrywanie konwencji.
4. `Llm` — wstawianie metod, weryfikacja build/test, pętla naprawcza.
5. Bootstrap nowych projektów testowych.
6. Scalanie, raport „po", bezpieczniki jakości.
