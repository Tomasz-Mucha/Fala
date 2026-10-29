# Fala

Odtwarzacz podcastów dla Windows 10/11 (WinUI 3, .NET 8). Interfejs po polsku, szwedzku i angielsku.

Darmowy i otwarty — na licencji [MIT](LICENSE).

## Funkcje

- **Subskrypcje** — wyszukiwarka katalogu Apple Podcasts, wklejenie adresu RSS/Atom albo strony z kanałem,
  import i eksport **OPML** (foldery z pliku stają się grupami).
- **Biblioteka** — grupy podcastów, filtr odcinków (wszystkie / nieodsłuchane / pobrane), kolejność,
  „oznacz wszystkie jako odsłuchane”.
- **Nowe odcinki** — sprawdzanie w tle co 1–12 godzin, licznik na kafelkach i w menu, powiadomienia Windows.
- **Odtwarzanie** — tempo 0,75–2×, wyłącznik czasowy, klawisze multimedialne i panel systemowy (SMTC),
  wznawianie od zapisanej pozycji, także po ponownym uruchomieniu aplikacji.
- **Kolejka** zapisywana między uruchomieniami, **historia** z wyszukiwarką, **zakładki** z komentarzami
  (eksport do Markdown).
- **Notatki odcinka** — znaczniki czasu (np. `12:34`) są klikalne i przewijają odcinek.
- **Pobieranie** odcinków do słuchania offline.

### Skróty klawiszowe

| Klawisz | Działanie |
|---|---|
| Spacja | odtwarzaj / pauza |
| ← / → | 15 s wstecz / 30 s naprzód |
| + / − | tempo |
| M | wycisz |
| Ctrl+F | wyszukiwanie |

## Instalacja

Z zakładki **Releases** pobierz `Fala-<wersja>-win-x64.zip` (albo `win-arm64` dla procesorów ARM),
rozpakuj i uruchom `Fala.exe`. Paczka zawiera wszystko, czego potrzebuje (.NET i Windows App SDK).

Wymagania: Windows 10 w wersji 2004 (19041) lub nowszej.

## Dane aplikacji

Wszystko jest trzymane lokalnie w `%LOCALAPPDATA%\Fala`:

| Plik / folder | Zawartość |
|---|---|
| `fala.db` | biblioteka, postęp, historia, zakładki, kolejka (SQLite) |
| `settings.json` | ustawienia |
| `Downloads\` | pobrane odcinki |
| `logs\` | dziennik z ostatnich 7 dni — przydatny przy zgłaszaniu błędów |

Zmienna środowiskowa `FALA_DATA_DIR` przekierowuje wszystkie dane w inne miejsce
(np. do testów na kopii danych albo do trybu przenośnego).

## Licencja

Fala jest udostępniana na licencji **MIT** — zobacz plik [`LICENSE`](LICENSE).
Copyright (c) 2026 Tomasz Mucha.

### Biblioteki zewnętrzne

Fala korzysta z bibliotek zewnętrznych na licencjach MIT, Apache-2.0, BSD i licencji Microsoft
(Windows App SDK). Ich teksty są w `Fala/THIRD-PARTY-NOTICES.txt` (dołączany do każdej paczki
i dostępny w Ustawieniach). Gotowe paczki zawierają komponenty Microsoft (.NET, Windows App SDK,
WebView2) objęte ich własnymi licencjami, nie licencją MIT. Po zmianie zależności:

```powershell
./tools/Generate-ThirdPartyNotices.ps1          # generuje plik z licencjami
./tools/Generate-ThirdPartyNotices.ps1 -Check   # sprawdza, czy każda zależność ma wpis (uruchamiane w CI)
```

## Budowanie

Wymagany **.NET SDK 10** (projekt celuje w .NET 8, ale używa C# 14).

```powershell
dotnet build Fala/Fala.csproj -p:Platform=x64
dotnet test Fala.Tests/Fala.Tests.csproj
```

Projekt `Fala.Tests` kompiluje logikę niezależną od interfejsu (RSS, OPML, wyszukiwanie, formatowanie)
bezpośrednio z plików aplikacji.

### Ochrona przed wyciekiem sekretów

Repozytorium ma hooki `pre-commit` i `pre-push`, które blokują klucze, tokeny, hasła, certyfikaty,
bazy danych i wyniki kompilacji. Po sklonowaniu włącz je raz:

```powershell
git config core.hooksPath .githooks
```

### Wydanie nowej wersji

```powershell
git tag v0.4.0
git push origin v0.4.0
```

GitHub Actions uruchamia testy, buduje paczki x64 i ARM64 i publikuje je w **Releases**.
Każdy push i pull request przechodzi testy i build (workflow `Build`).
