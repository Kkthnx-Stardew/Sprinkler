# Translating Sprinkler

Sprinkler ships in 17 languages: English, Spanish, French, German, Brazilian Portuguese, Italian, Dutch, Russian, Ukrainian, Polish, Turkish, Hungarian, Bulgarian, Simplified Chinese, Japanese, Korean and Thai. English is the source. The other 16 were written with machine help and have not been read by native speakers yet, so some of them will sound odd. Corrections are the most useful thing you can send.

## How the files work

Every language is one flat JSON file in [`src/Sprinkler.Core/Locales`](../src/Sprinkler.Core/Locales), named by its code (`de.json`, `pt-BR.json`, `zh-Hans.json`). Each line is a key and its text:

```json
"act.removed": "{0} was removed. Undo it from History if you change your mind."
```

- `{0}`, `{1}` stand for values the app fills in, such as a mod name or a count. Keep every one of them, and you may move them around to fit your grammar.
- A key that ends in `_one`, `_few`, `_many` or `_other` is a counted phrase. Which forms your language needs follows the Unicode CLDR rules: English, Spanish, German, Italian, Dutch, Turkish, Hungarian and Bulgarian use `one` and `other`; French and Brazilian Portuguese use `one` for 0 and 1 and `other` for the rest; Russian, Ukrainian and Polish use `one`, `few` and `many`; Chinese, Japanese, Korean and Thai only need `other`. The `_one` form may leave out `{0}` if your language says "a" instead of "1".
- `fmt.datetime` is the date and time pattern (`yyyy` year, `MM` month, `dd` day, `HH` hour, `mm` minute). Use numbers, not month names.
- `core.sum_join` and `core.sum_end` join the pieces of the summary shown after an update ("3 done, 1 failed."). Use your language's own punctuation.
- Keep product names as they are: Sprinkler, SMAPI, Nexus Mods, CurseForge, GitHub, Stardew Valley, and the Nexus button names (Manual Download, Slow Download, Mod Manager Download), because those are what the user sees on the website.
- Write plain sentences. No em dashes. Say what happened and what to do next, the way the English does.

If a key is ever missing at run time, the app shows the English text for it instead of a gap, and empty values count as missing. The tests are stricter: every language file must carry every key, so when a new English text is added, each file gets the English text as a placeholder until someone translates it.

## Fixing or adding a language

1. Edit the file for your language (or copy `en.json` to a new name).
2. If it is a new language, add it to the `Loc.All` list in [`Loc.cs`](../src/Sprinkler.Core/Loc.cs) with its native name and plural rule, and add its system locale names to `Loc.Match` if they need special handling.
3. Run `dotnet test tests/Sprinkler.Tests`. The locale tests check that every key exists, that placeholders match English, that counted phrases have the forms your language needs, that dates format, and that nothing is empty or uses an em dash.
4. To see it on screen, start the app with `--lang <code>`, for example `Sprinkler --lang de --tab help`.
5. Open a pull request with the file and, if you can, a line saying whether you are a native speaker.

The `ui.*` keys come from the screens, `core.*` from messages the app writes while it works, and the others from the pieces in between. English texts can change between releases; the tests tell you what is new.

## Fonts

The app's own font does not include Chinese, Japanese, Korean or Thai. Those languages use a font from your system, which Windows and most Linux desktops provide. If a Linux install shows empty boxes, install a CJK or Thai font package such as Noto.
