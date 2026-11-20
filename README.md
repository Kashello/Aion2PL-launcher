# Aion2PL — spolszczenie AION 2

Nieoficjalne spolszczenie AION 2 autorstwa **kashello**. Launcher pobiera polskie tłumaczenie i pomaga je zainstalować w wersji gry ze Steam.

## Pobieranie

**[Pobierz najnowszy launcher Aion2PL.exe](https://github.com/Kashello/Aion2PL-launcher/releases/latest)**

Na stronie wydania plik znajduje się w sekcji **Assets**. Launcher działa w systemie Windows i nie wymaga logowania ani instalowania Pythona.

## Instalacja spolszczenia

1. Zamknąć AION 2 i uruchomić `Aion2PL.exe`.
2. Jeśli launcher nie znajdzie gry, wskazać folder instalacyjny AION 2.
3. Sprawdzić informację o zgodności wersji gry i spolszczenia, a następnie wybrać **Zainstaluj spolszczenie**.
4. Po zakończeniu uruchomić grę i w ustawieniach wybrać język tekstu **Polski**.

Dla Steam build 26728906 launcher 0.7.0 instaluje polskie zasoby tekstowe Engine.locres i ustawienia języka. W ustawieniach tekstu „Polski” zajmuje miejsce angielskiego. Na ekranie startowym może pozostać „Click the screen”. Działanie tłumaczenia sprawdzisz w dzienniku zadań. Launcher obsługuje również wcześniejsze paczki dla slotu francuskiego.

Starsza metoda (plik `L10NString.dat` w folderze `en-US` i wyłączony angielski PAK) po tym patchu pokazuje w grze „???”. Launcher 0.6.0 wykrywa jej pozostałości. Proponuje usunięcie luźnego DAT-a (kopia zostaje w historii launchera) i przywraca angielski PAK ze swojej kopii. Gdy kopii nie ma, poprosi o sprawdzenie spójności plików w Steam przed instalacją.

### Najczęstsze pytania

- **Gdzie jest polski?** W ustawieniach gry na liście języka tekstu wybierz **Polski**.
- **Niektóre okna lub banery są po francusku.** To znane. Te teksty pochodzą z innych plików silnika gry, których spolszczenie nie zmienia.
- **Po aktualizacji gry spolszczenie zniknęło.** Steam przywrócił oryginalny plik. Launcher pokaże to w stanie instalacji. Poczekaj na paczkę zgodną z nowym buildem gry i zainstaluj ją ponownie.
- **Launcher nie chce instalować („nie jest oryginałem Steam”).** Plik francuski w grze został wcześniej zmieniony (np. ręcznie lub innym spolszczeniem). Sprawdź spójność plików gry w Steam i spróbuj ponownie.

## Aktualizacje

Launcher sprawdza dostępność spolszczenia na [kashello.pl](https://kashello.pl) oraz nowych wersji launchera na GitHubie. Po aktualizacji gry należy poczekać na paczkę zgodną z nową wersją AION 2.

Przycisk **Sprawdź launcher** pozwala w dowolnej chwili sprawdzić jego aktualizacje i wyświetlić informację o bieżącym stanie.

**Ważne:** użytkownicy launcherów starszych niż 0.5.4 muszą pobrać nowy plik EXE ręcznie jeden raz. Wersja 0.5.4 potrafi automatycznie pobierać kolejne wydania launchera. Paczki po patchu 26429009 wymagają launchera 0.6.0 — starsze launchery ich nie zainstalują.

## Przywracanie oryginału (cofnięcie spolszczenia)

Po zamknięciu gry wybrać w launcherze **Przywróć oryginał** i postępować zgodnie z wyświetlanymi komunikatami. Potem w grze wybrać z powrotem dowolny język. Launcher przywraca oryginalny francuski PAK z kopii i poprzedni `UserGame.ini`. Jeśli potrzebnych plików nie uda się odtworzyć z kopii (np. spolszczenie wgrano ręcznie), launcher wskaże weryfikację plików gry przez Steam.

## Zgłaszanie błędów

Błędy tłumaczenia najlepiej zgłaszać razem ze zrzutem ekranu i nazwą zadania lub miejsca. Informacje o projekcie i aktualizacjach znajdują się na [kashello.pl](https://kashello.pl).

Spolszczenie jest nieoficjalne i korzystanie z niego odbywa się na własną odpowiedzialność.
