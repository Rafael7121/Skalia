---
version: alpha
name: SKALIA
description: A dark, direct launcher for playing Unturned offline with bots. One accent color (the beige of the hat) is kept for the play action.
colors:
  sidebar: "#0D0E12"
  window: "#13151A"
  card: "#1C1F26"
  input: "#262A33"
  input-border: "#383D49"
  hover: "#22252D"
  disabled: "#2C2F37"
  toggle-off: "#3E434F"
  text: "#EBECF0"
  muted: "#9298A6"
  accent: "#D6C08C"
  accent-hover: "#E6D2A2"
  on-accent: "#181610"
  good: "#6ECF8C"
  warn: "#E8B85C"
typography:
  brand:
    fontFamily: Segoe UI
    fontSize: 22pt
    fontWeight: 700
  page-title:
    fontFamily: Segoe UI
    fontSize: 20pt
    fontWeight: 700
  page-subtitle:
    fontFamily: Segoe UI
    fontSize: 10pt
    fontWeight: 400
  section-label:
    fontFamily: Segoe UI
    fontSize: 10.5pt
    fontWeight: 600
  field-label:
    fontFamily: Segoe UI
    fontSize: 10.5pt
    fontWeight: 400
  body:
    fontFamily: Segoe UI
    fontSize: 10.5pt
    fontWeight: 400
  hint:
    fontFamily: Segoe UI
    fontSize: 9pt
    fontWeight: 400
  nav:
    fontFamily: Segoe UI Semibold
    fontSize: 10.5pt
    fontWeight: 600
  button-primary:
    fontFamily: Segoe UI
    fontSize: 12pt
    fontWeight: 700
  button-secondary:
    fontFamily: Segoe UI Semibold
    fontSize: 10pt
    fontWeight: 600
rounded:
  nav: 7px
  button: 8px
  toggle: 11px
spacing:
  xs: 4px
  sm: 8px
  md: 12px
  lg: 18px
  xl: 24px
  page-x: 36px
  page-top: 28px
  page-bottom: 32px
  sidebar-x: 24px
  card-x: 24px
  card-y: 20px
  row-gap: 12px
  card-gap: 12px
  section-gap: 24px
components:
  sidebar:
    backgroundColor: "{colors.sidebar}"
    width: 250px
  nav-item:
    backgroundColor: "{colors.sidebar}"
    textColor: "{colors.muted}"
    typography: "{typography.nav}"
    rounded: "{rounded.nav}"
    height: 42px
  nav-item-hover:
    backgroundColor: "{colors.hover}"
  nav-item-selected:
    backgroundColor: "{colors.card}"
    textColor: "{colors.text}"
  card:
    backgroundColor: "{colors.card}"
    padding: 24px
  input:
    backgroundColor: "{colors.input}"
    textColor: "{colors.text}"
    typography: "{typography.body}"
    height: 28px
    width: 370px
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
    typography: "{typography.button-primary}"
    rounded: "{rounded.button}"
    height: 52px
  button-primary-hover:
    backgroundColor: "{colors.accent-hover}"
  button-primary-disabled:
    backgroundColor: "{colors.disabled}"
    textColor: "{colors.muted}"
  button-secondary:
    backgroundColor: "{colors.input}"
    textColor: "{colors.text}"
    typography: "{typography.button-secondary}"
    rounded: "{rounded.button}"
    height: 38px
  toggle-on:
    backgroundColor: "{colors.accent}"
    rounded: "{rounded.toggle}"
    size: 40px
  toggle-off:
    backgroundColor: "{colors.toggle-off}"
    rounded: "{rounded.toggle}"
    size: 40px
  slider:
    trackColor: "{colors.input-border}"
    fillColor: "{colors.accent}"
    knobColor: "{colors.accent}"
    height: 28px
  tab:
    textColor: "{colors.muted}"
    typography: "{typography.body}"
    height: 34px
  tab-selected:
    textColor: "{colors.text}"
    underlineColor: "{colors.accent}"
---

# SKALIA

The source of truth for how the launcher looks. The tokens above mirror `src/Skalia.Launcher/Theme.cs`; when one changes, the other changes with it. Format: [DESIGN.md](https://github.com/google-labs-code/design.md).

## Overview

SKALIA is the screen between the player and Unturned: pick a map, adjust the server, press PLAY. Whoever opens it wants to be playing within seconds, so each screen has one main action and everything else is optional setup.

- **Dark for a reason.** The launcher sits next to a game, often at night and full screen. A light theme would glare on every window switch.
- **One action per screen.** PLAY / JOIN GAME is the only accent-colored thing that asks for a click. Everything else is neutral.
- **Simple first.** On the Server page, what everyone touches (name, map, difficulty, loot, zombies, PvP) is at the top. Bots and fine rules live under "Advanced settings", closed by default.
- **No developer tooling in the player's face.** Internal plugins, recording, admin commands and log switches stay out of the main flow; the few developer switches live in a closed "Developer" section.

## Colors

Four layers of bluish grays, plus one beige taken from the hat in the icon.

- **sidebar (#0D0E12)** is the deepest layer: the sidebar and the console background.
- **window (#13151A)** is the page background.
- **card (#1C1F26)** groups related fields. It is the only "elevation" in the app.
- **input (#262A33)** marks what can be edited: text boxes, dropdowns and secondary buttons.
- **text (#EBECF0)** is the main text; **muted (#9298A6)** is for labels, hints and unselected menu items.
- **accent (#D6C08C)** is the hat's beige, SKALIA's identity, and **on-accent (#181610)** is the text on top of it.
- **good (#6ECF8C)** and **warn (#E8B85C)** only appear in the server status (online / loading, saving).

Rules for the accent:

- It goes on the **main button** (PLAY / JOIN GAME), **switches that are on**, the **selected menu item and tab marker**, and the **filled part of a slider**. Each of these marks a real action or state.
- It does **not** go on section titles, text links, decorative icons or borders. Titles are `text`; the expand link is `muted` with an arrow.
- good and warn never become decoration: each use matches a server state.

Measured contrast (WCAG): text on card 13.97:1; muted on card 5.7:1; muted on input 4.97:1; on-accent on accent 10.14:1. No text below 4.5:1.

## Typography

Segoe UI, Windows' own font. No font file to ship, it renders sharp with ClearType and matches the system windows around it.

- **page-title (20pt bold):** one per page ("Server", "Gameplay", "Plugins & Workshop", "Console").
- **section-label (10.5pt semibold, text color):** card titles ("Bots", "Bot difficulty", "World"). Normal case, no wide tracking.
- **field-label / body (10.5pt):** label on the left, value on the right.
- **hint (9pt, muted):** one line under a field, only when it adds something the label does not say.
- **button-primary (12pt bold, uppercase):** PLAY and JOIN GAME are the only uppercase text, because they are the verb of the screen.

All text goes through `L.Tr()`. English is the default and Brazilian Portuguese is optional, so layouts must hold text up to ~30% longer.

## Layout

One grid, defined in `Theme.cs` and `MainForm.cs` (constants `InputHeight`, `LabelWidth`, `FieldWidth`, `CardPadX`, `RowGap`, `CardGap`, `SectionGap`). No loose numbers in the middle of the layout.

- A **fixed 250px sidebar** on the left: logo, navigation (4 items), language, server status and the main button at the bottom. Everything in the sidebar starts at **x = 24**, menu text included. The play button is always in the same place on every page; the footer shrinks when the stop button goes away, without leaving a gap.
- Content has **36px** side margins, **28px** at the top and **32px** at the end. The content width is the card's (**618px**): a wide window leaves space on the right, never in the middle of the cards. This also holds when the scrollbar shows up.
- **Card**: 24px of padding on the sides and 20px top and bottom. A two-column grid: label (**200px**, wraps if needed) and field (**370px**). Every single-line field (text, number, dropdown) is **28px tall and 370px wide**; multi-line boxes have the same width. Switches take the whole row (570px).
- **Rows**: 12px apart. The label is centered on the field's height. A field's hint sits 4px below it; a switch's hint sits right under its title.
- **Order inside a card**: title, fields, switches. Exception: a field that depends on a switch comes right below it (for example "Beds per player" below /home).
- **Between blocks**: 12px between cards; 24px before a collapsible section title; the collapsible title is 32px tall.
- A page longer than the window scrolls; the sidebar never scrolls.

## Elevation & Depth

No shadow, no blur, no glow. Depth comes only from the change of color layer (sidebar → window → card → input). A card is never inside another card; tabs inside a card switch rows, they do not nest cards.

## Shapes

- Buttons: 8px radius. Menu item: 7px radius. Switch: pill track (11px radius). Slider knob: circle.
- Cards and text fields are square. Only what is clicked is rounded, which helps tell actions from content.

## Motion & Micro-interaction

Hover and focus states only, switched instantly (no animation). Nothing pulses, blinks or loops. The status dot changes color when the server changes state, and that is all.

## Voice & Content Strategy

- Talk to someone who wants to play, not to a server admin. "Loot amount", not "Items/Spawn_Chance".
- Multipliers show as "0.5x / 1x / 2x"; 1x is always the default (of the chosen difficulty, or of how the recorded players play, for the bots).
- Status says what is happening and what to do: "Loading map... This takes a moment." / "Server online. Click JOIN GAME."
- An error says what failed and the next step, never just "Error".
- No emoji, no exclamation marks, no OpenMod or Unturned internals.
- Honest placeholders: an empty bot list says "Empty = names generated automatically", never fake example names.

## Components

All hand-drawn in WinForms (`UserPaint`), with no default Windows styles.

- **FlatButton** → `@src/Skalia.Launcher/Theme.cs#FlatButton` (`button-primary`, `button-secondary`). Disabled while the action is not possible (JOIN GAME only unlocks once the server is ready).
- **NavButton** → `@src/Skalia.Launcher/Theme.cs#NavButton` (`nav-item`). The selected one gets a `card` background, `text` color and a 3px vertical `accent` bar on the left that marks the current page.
- **ToggleSwitch** → `@src/Skalia.Launcher/Theme.cs#ToggleSwitch` (`toggle-on`, `toggle-off`). Label beside it and an optional hint below; the control for any on/off option.
- **Dropdowns and fields** → `Theme.StyleInput` (`input`). `Dropdown` replaces the ComboBox: closed it is an input background, a 1px border and a chevron; open it is a dark list of 28px rows, up to 10 rows, with the current option marked by a small `muted` bar. `InputFrame` gives text boxes the 1px border instead of the native one. `NumberBox` replaces the NumericUpDown (no native arrows; the up and down keys change the value). The border lightens to `muted` on focus, never to `accent`.
- **Slider** → `@src/Skalia.Launcher/Slider.cs#Slider` (`slider`), for multipliers (bot difficulty). It sits right on the card, with no field background. A 4px `input-border` track, a 16px `accent` knob and the value ("1.25x") right-aligned in a 56px column. A small `muted` tick marks 1x, which sits in the middle of the track, and the stretch between it and the knob is `accent`. 0.05x steps; the arrow keys and Home/End change the value.
- **Tabs inside a card** → `@src/Skalia.Launcher/TabStrip.cs#TabStrip` (`tab`, `tab-selected`), for the bot modifiers. `muted` text, the selected tab in `text` with a 2px `accent` underline, a 1px `input-border` line under all of them, 20px between tabs, lined up with the label column. Each tab shows only its own rows of the card; a tab with no content does not appear.
- **Windows parts** → scrollbars and the title bar use Windows 10/11's own dark mode (`Theme.DarkScrollbars`, `Theme.DarkTitleBar`). No light native control on screen.
- **Card** → `MainForm.NewCard` (`card`). One subject per card, with an optional title. Under "Advanced settings" each group (Bots, Bot difficulty, World, Survival, Interface and combat, Server) is its own card.
- **Collapsible section** → "Advanced settings" and "Developer": `muted` text with an arrow, closed by default.

## Do's and Don'ts

**Do**

- Keep the beige for the play button, switches that are on, the current menu item or tab, and slider fills.
- Put every new option under "Advanced settings" first; it only moves up to the basic card if most players will touch it.
- Keep labels short and explain the rest in a one-line hint, only if the hint says something new.
- Test in English and in Portuguese, and with the server stopped, loading and online.

**Don't**

- Don't use colored uppercase in section titles.
- Don't repeat the same hint under several fields: if it applies to the group, it goes once at the top of the group.
- Don't use gradients, shadows, glow, emoji or looping animation.
- Don't show internal plugins, technical IDs or file paths, except in the Console.
- Don't add a control that does nothing. If the feature doesn't exist, the control doesn't appear.
