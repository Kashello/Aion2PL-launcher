# Aion2PL by kashello — launcher 0.5.4

## Zmiany 0.5.4

- Launcher dodaje do `Aion2/Config/UserGame.ini` wyłącznie brakujące mapowania kultury, które użytkownik sprawdził w grze dla znaku „ó”. Istniejące ustawienia są zachowywane; sprzeczne mapowania wstrzymują zmianę. Cofnięcie usuwa jedynie blok dodany przez launcher.
- Launcher osobno sprawdza najnowsze wydanie w publicznym repozytorium GitHub `Kashello/Aion2PL-launcher`. Nową wersję pliku `Aion2PL.exe` weryfikuje według rozmiaru i sumy SHA-256 podawanej przez GitHub, zachowuje kopię poprzedniego EXE i uruchamia się ponownie. Awaria GitHuba nie uniemożliwia sprawdzania paczek na kashello.pl.
- Wydanie GitHub musi być publicznym, stabilnym Release z tagiem w formacie `v0.5.4` oraz jednym załącznikiem nazwanym dokładnie `Aion2PL.exe`. Nie publikować nowego EXE pod tym samym tagiem.
- Wersje wcześniejsze niż 0.5.4 nie mają samodzielnej aktualizacji: użytkownicy muszą pobrać nowy EXE raz, późniejsze wydania mogą już pobierać się automatycznie.

## Zmiany 0.5.3

- Licencje czcionek otwierają się domyślnie po polsku: pełne nieoficjalne tłumaczenie pomocnicze OFL 1.1 oraz informacje o autorach. Niezmienione angielskie oryginały i copyright pozostają w osobnej zakładce „Oryginały”. Licencja dotyczy czcionek, nie całej aplikacji.

## Ważna informacja od kashello

Używasz na własną odpowiedzialność — nie mogę zagwarantować braku bana. Dodajemy plik z polskimi tekstami i wyłączamy angielski PAK przez zmianę jego nazwy, aby gra wczytała spolszczenie. Nie zmieniamy zawartości oryginalnych plików gry ani plików EXE/DLL.

Od premiery sam używam spolszczenia i dotychczas nie otrzymałem blokady konta. To moje doświadczenie, a nie gwarancja, że blokada nie wystąpi w przyszłości lub u innych graczy.

## Zmiany 0.5.2

- Widoczna informacja o korzystaniu na własną odpowiedzialność; kliknięcie otwiera pełną notkę od kashello, w tym jego relację o dotychczasowym braku blokady konta. Bez dodatkowego obowiązkowego potwierdzenia.

## Zmiany 0.5.1

- Usunięto metadane pochodzenia C2PA (sekcje caBX z certyfikatami Trufo) z obu PNG w Assets. Bez zmiany danych obrazu, przezroczystości i wyglądu; oryginalne grafiki pozostają poza projektem.

## Zmiany 0.5

- Własna ikona kota kashello z polską flagą, osadzona w EXE i oknie aplikacji. Wielkości ICO: 16, 24, 32, 48, 64, 128 i 256 px. Źródło PNG z przezroczystością w Assets/aion2pl-icon.png.

Samodzielna aplikacja Windows x64, C# / .NET 10 / Windows Forms. Logo osadzone w EXE; brak zależności od edytora, Pythona i logowania. Fioletowe kolory motywu ze strony kashello.pl, czytelne przyciski, opcjonalny panel szczegółów działań oraz zweryfikowane linki kashello.pl / Twitch / YouTube / Discord. Linki otwierają się dopiero po kliknięciu.

## Zmiany 0.3

- Wyraźna sekcja: Build Twojej gry, Build spolszczenia i opis zgodności. Zielony = zgodne wydanie, czerwony = brak zgodnej paczki, żółty = oczekiwanie na dane/aktualizacja Steam/problem połączenia. Opisy pozostają czytelne niezależnie od koloru.
- Kolejność: odczyt buildu Steam, pobranie metadanych, porównanie buildów, dopiero potem sprawdzanie istniejącego spolszczenia. Zła wersja nie powoduje pobrania ZIP ani możliwości instalacji.
- Ręczne wgranie bez stanu launchera: porównanie wszystkich plików lokalizacji ze zweryfikowanym ZIP-em dla buildu gry. Zgodne pliki przy nieaktywnym angielskim PAK oznaczają „już zainstalowane”; bez ponownego zapisu, kopii i udawania instalacji. Samo istnienie DAT nie wystarcza. Zgodny DAT z aktywnym angielskim PAK nadal wymaga naprawy.
- Przy pierwszej kontroli ręcznej instalacji pobierana jest paczka do porównania bez zmian w grze. Zweryfikowane ZIP-y są zachowywane w LocalAppData/Aion2PL/packages/<SHA256>.zip, sprawdzane przed ponownym użyciem, bez kolejnego pobierania przy każdym odświeżeniu. Ręczna instalacja nie tworzy historii przywracania launchera.

## Zmiany 0.4

- Wykrywane są ręczne .pak.backup/.backup2 oraz kopie .aion2pl-<build>-<id>.backup. Zarządzana kopia o potwierdzonym SHA i bieżącym buildzie ma pierwszeństwo. Gdy jej brak, przycisk „Przywróć z kopii PAK” pozwala wybrać ręczną kopię; przy kilku plikach pokazujemy wybór. Przed przywróceniem ręcznej kopii użytkownik otrzymuje informację, że jej build jest nieznany, i potwierdza wybór. Kontrola rozmiaru/stopki PAK przed zapisem; kopia pozostaje zachowana. Nie udajemy potwierdzonej zgodności jej buildu.
- Aktualizacja Steam odtwarzająca aktywny PAK oznacza naprawę spolszczenia, a nie użycie starej kopii. Instalacja zachowuje nowy aktywny PAK pod nową, unikalną nazwą przypisaną do aktualnego buildu. Stare kopie pozostają. Przywracanie nigdy nie nadpisuje już aktywnego PAK Steam starym backupem. Test pełnego cyklu patcha sprawdza oba backupy i przywrócenie nowego oryginału.
- Jeśli żadnej kopii nie ma, dostępny jest „Przywróć oryginał przez Steam”. Dopiero po potwierdzeniu użytkownika otwierany jest steam://validate/3393110; launcher nie zmienia wtedy plików gry. Po weryfikacji użytkownik klika Odśwież i Przywróć oryginał.
- Po odtworzeniu oryginalnego PAK można cofnąć również ręcznie zainstalowane pliki zgodne ze sprawdzoną paczką (z kopią przed usunięciem). Poprawiono migrację stanu z 0.2: gdy poprzedni DAT był identycznym spolszczeniem, nie wgrywamy go ponownie jako „oryginału”.
- Nowa typografia: niezmodyfikowane statyczne Cinzel Bold (NDISCOVER/Cinzel) i Inter Regular/Bold 4.1 (rsms/inter). Czcionki oraz pełne informacje copyright/licencje SIL OFL 1.1 są zasobami EXE; link Licencje udostępnia je w aplikacji. Rejestracja fontów wyłącznie w procesie, bez instalowania w Windows. Zaokrąglone przyciski, wyróżniony główny przycisk gradientem, stany hover/pressed/disabled/focus i czytelna klawiaturowa nawigacja.

## Aktualizacje

- Publiczny GET https://kashello.pl/api/aion2pl/latest przy starcie i co 3 godziny. Przycisk odświeżania odczytuje bieżący stan gry; nie omija 3-godzinnego cache zapytania. Brak tokenów, cookies, wysyłania buildu lub ścieżek użytkownika na serwer.
- Version parsowane jako DateOnly w formacie dd.MM.yyyy. Większa data oznacza aktualizację; równa data nie jest nowym wydaniem. Naprawa brakujących plików/przywróconego przez Steam PAK jest możliwa tą samą paczką. Zmiana zawartości opublikowanego ZIP wymaga nowszej daty wydania; nie podmieniamy paczki pod tą samą datą.
- Decyzja właściciela: różny build BEZWZGLĘDNIE blokuje instalację. Aktualizacja Steam w toku i uruchomiona gra również blokują zmiany. Build i procesy ponownie sprawdzane tuż przed zapisem i pomiędzy plikami.
- Download URL wyłącznie z odpowiedzi; HTTPS wymagany także przy przekierowaniach. 404 wyświetla error z serwera; inne błędy nie zmieniają gry. Pobieranie maks. 256 MiB, metadata 64 KiB; timeout metadata 20 s / pobierania 5 min. Rozmiar i SHA-256 potwierdzone PRZED odczytem ZIP; niepoprawny plik pobrania usuwany.
- ZIP: odrzucenie ścieżek bezwzględnych, .., ADS, nazw urządzeń Windows, dowiązań i zduplikowanych nazw. CRC każdej pozycji i limity rozpakowania (128 MiB na plik, 256 MiB razem, 2000 wpisów). Brak stałej listy trzech pozycji: dokumenty sprawdzane i pomijane przy instalacji, dodatkowe pliki lokalizacji obsługiwane dynamicznie. Zapis z paczki ograniczony do Aion2/Content/L10N; próba zapisu w innym miejscu gry lub EXE/DLL/skryptu jest odrzucana. Rozszerzenie zakresu przyszłych paczek wymaga świadomej zmiany launchera.

## Kopie i cofanie

- Stan i kopie sprzed zapisu: %LOCALAPPDATA%/Aion2PL/installations/<hash-folderu-gry>/. Ręcznie wybrany folder: game-folder.txt obok. Panel logu pozostaje lokalnie w pamięci, bez wysyłania na serwer.
- Aktualny aktywny angielski PAK zachowywany pod unikalną nazwą .pak.aion2pl-<build>-<id>.backup w tym samym folderze gry; istniejące kopie nie są nadpisywane. Nie zmieniamy .sig/.utoc/.ucas, EXE/DLL ani fontów.
- Zapis przez pliki tymczasowe w tym samym katalogu, kopie z hashami, dziennik pending.json przed pierwszą zmianą, cofanie w razie błędu. Przerwana operacja blokuje nową instalację; przycisk przywracania pozwala ją cofnąć po ponownym uruchomieniu. Nie nadpisujemy plików zmienionych później przez Steam/użytkownika; kopie zostają do sprawdzenia. Brak automatycznej modyfikacji gry przy samym starcie launchera.
- Przywracanie pozostawia aktywny PAK Steam. Przy jego braku preferuje własną kopię bieżącego buildu; ręczna kopia o nieznanym buildzie wymaga jawnego wyboru/potwierdzenia. Brak kopii prowadzi do weryfikacji Steam. Poprzedni DAT jest zachowany; jeśli był już ręcznym spolszczeniem, nie przywracamy go jako oryginału (flaga pozostaje również po kolejnych aktualizacjach). Kopie zachowane w historii pozwalają odzyskać pliki.
- Brak dostępu do zapisu: komunikat z instrukcją ponownego uruchomienia jako administrator. Nie wymuszamy podniesienia uprawnień przy odczycie. Mechanizm aktualizacji samego EXE opisano w zmianach 0.5.4 powyżej.

## Budowanie i weryfikacja

`dotnet publish Aion2PL.csproj -c Release -o publish` — SDK .NET 10; wynik pojedynczy EXE z runtime, użytkownik niczego nie instaluje.

Sprawdzenia (folder wynikowy musi istnieć; przy wywołaniu WinExe z PowerShell użyj Start-Process -Wait):

- `Aion2PL.exe --self-test C:/testy/tests.json` — wykrywanie Steam oraz testy transportu, walidacji i instalacji na fikcyjnej grze, bez zmian prawdziwej instalacji.
- `Aion2PL.exe --inspect C:/testy/game.json` — odczyt lokalnej instalacji.
- `Aion2PL.exe --verify-live C:/testy/live.json` — prawdziwy endpoint i pobranie ZIP, pełna kontrola sumy/rozmiaru/ZIP bez instalacji. Zachowuje pobranie obok raportu.
- `Aion2PL.exe --preview C:/testy/preview.png --details` — render własnego okna bez zapytań sieciowych; opcjonalny rozwinięty panel szczegółów.

Rzeczywista instalacja, przywracanie i test gry z launcherem wymagają oddzielnego testu użytkownika; testy automatyczne działają na fixture. Nie podpisano EXE i nie testowano na drugim komputerze.
