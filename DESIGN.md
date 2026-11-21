---
version: alpha
name: SKALIA
description: Launcher escuro e direto para jogar Unturned offline com bots. Uma cor de destaque (bege do chapéu) reservada para a ação de jogar.
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
---

# SKALIA

Fonte da verdade do visual do launcher. Os tokens acima espelham `src/Skalia.Launcher/Theme.cs`; se um mudar, o outro muda junto. Formato: [DESIGN.md](https://github.com/google-labs-code/design.md).

## Overview

O SKALIA é a tela que fica entre o jogador e o Unturned: escolher mapa, ajustar o servidor e apertar PLAY. Quem abre quer jogar em segundos, então a tela tem uma ação principal e todo o resto é configuração opcional.

- **Escuro por motivo, não por moda.** O launcher abre ao lado de um jogo e é usado à noite, em tela cheia, junto do Unturned. Um tema claro ofuscaria na troca de janela.
- **Uma ação por tela.** O botão PLAY / JOIN GAME é a única coisa em cor de destaque que pede clique. O resto é neutro.
- **O simples primeiro.** Na página Servidor, o que todo mundo mexe (nome, mapa, dificuldade, loot, zumbis, PvP) fica no topo; bots e regras finas ficam dentro de "Configurações avançadas", fechado por padrão.
- **Nada de dev na cara do jogador.** Plugins internos (SKALIA DEPS), gravação, comandos e nomes de outros projetos não aparecem na interface.

## Colors

Escala de cinzas azulados em quatro camadas, mais um bege tirado do chapéu do ícone.

- **sidebar (#0D0E12)** é a camada mais funda: barra lateral e fundo do console.
- **window (#13151A)** é o fundo das páginas.
- **card (#1C1F26)** agrupa campos relacionados. É a única "elevação" do app.
- **input (#262A33)** marca o que é editável: caixas, combos e botões secundários.
- **text (#EBECF0)** é o texto principal; **muted (#9298A6)** é para rótulos, dicas e itens de menu não selecionados.
- **accent (#D6C08C)** é o bege do chapéu, a identidade do SKALIA, e **on-accent (#181610)** é o texto em cima dele.
- **good (#6ECF8C)** e **warn (#E8B85C)** só aparecem no status do servidor (online / carregando, salvando).

Regras da cor de destaque:

- Ela vai no **botão principal** (PLAY / JOIN GAME), nos **toggles ligados** e no **marcador do item selecionado** do menu. Esses três marcam ação ou estado real.
- Ela **não** vai em títulos de seção, links de texto, ícones decorativos ou bordas. Título é `text`; link de expandir é `muted` com seta.
- good e warn nunca viram decoração: cada uso corresponde a um estado do servidor.

Contraste medido (WCAG): text em card 13,97:1; muted em card 5,7:1; muted em input 4,97:1; on-accent em accent 10,14:1. Nada abaixo de 4,5:1 para texto.

## Typography

Segoe UI, a fonte do próprio Windows. Não precisa embutir arquivo de fonte, renderiza nítida com ClearType e combina com as janelas do sistema ao redor.

- **page-title (20pt bold):** um por página ("Server", "Plugins & Workshop", "Console").
- **section-label (10,5pt semibold, cor text):** título dos cards ("Bots", "Regras", "Plugins"). Escrito normal, sem caixa alta e sem espaçamento largo.
- **field-label / body (10,5pt):** rótulo à esquerda, valor à direita.
- **hint (9pt, muted):** uma linha embaixo do campo, só quando acrescenta algo que o rótulo não diz.
- **button-primary (12pt bold, caixa alta):** PLAY e JOIN GAME são os únicos textos em caixa alta, porque são o verbo da tela.

Todo texto passa por `L.Tr()`. Inglês é o padrão e português (Brasil) é opcional, então os layouts precisam aguentar textos até ~30% maiores.

## Layout

Uma grade só, definida em `Theme.cs` e `MainForm.cs` (constantes `InputHeight`, `LabelWidth`, `FieldWidth`, `CardPadX`, `RowGap`, `CardGap`, `SectionGap`). Nada de número solto no meio do layout.

- Janela com **sidebar fixa de 250px** à esquerda: logo, navegação (4 itens), idioma, status do servidor e o botão principal no rodapé. Tudo na sidebar começa em **x = 24**, inclusive o texto do menu. O botão de jogar fica sempre no mesmo lugar, em qualquer página; o rodapé encolhe quando o botão de parar some, sem deixar buraco.
- Conteúdo com margem de **36px** nas laterais, **28px** no topo e **32px** no fim. A largura do conteúdo é a do card (**618px**): em janela larga sobra espaço à direita, nunca no meio dos cards. Isso vale também quando a barra de rolagem aparece.
- **Card**: respiro de 24px nas laterais e 20px em cima e embaixo. Grade de duas colunas: rótulo (**200px**, quebra a linha se precisar) e campo (**370px**). Todo campo de uma linha (texto, número, lista) tem **28px de altura e 370px de largura**; caixas de várias linhas têm a mesma largura. Interruptores ocupam a linha toda (570px).
- **Linhas**: 12px entre uma e outra. O rótulo fica centrado na altura do campo. A dica de um campo fica 4px abaixo dele; a de um interruptor, logo abaixo do título.
- **Ordem dentro do card**: título, campos, interruptores. Exceção: um campo que depende de um interruptor vem logo abaixo dele (ex.: "Camas por jogador" abaixo de /home).
- **Entre blocos**: 12px entre cards; 24px antes de um título de seção recolhível; o título recolhível tem 32px de altura.
- Página mais longa que a janela rola; a sidebar nunca rola.

## Elevation & Depth

Sem sombra, sem blur, sem brilho. A profundidade vem só da troca de camada de cor (sidebar → window → card → input). Um card nunca fica dentro de outro card.

## Shapes

- Botões: raio 8px. Item de menu: raio 7px. Toggle: trilho em pílula (raio 11px).
- Cards e campos de texto são retos. Só o que se clica é arredondado, e isso ajuda a separar o que é ação do que é conteúdo.

## Motion & Micro-interaction

Só estados de hover e de foco, trocados na hora (sem animação). Nenhum elemento pulsa, pisca ou anima em loop. A bolinha de status muda de cor quando o servidor muda de estado, e é só isso.

## Voice & Content Strategy

- Fala com quem quer jogar, não com quem administra servidor. "Loot amount", não "Items/Spawn_Chance".
- Multiplicadores aparecem como "0.5x / 1x / 2x"; 1x é sempre o padrão da dificuldade escolhida.
- Status diz o que está acontecendo e o que fazer: "Loading map... This takes a moment." / "Server online. Click JOIN GAME."
- Erro diz o que falhou e o próximo passo, nunca só "Erro".
- Sem emoji, sem exclamação, sem jargão de OpenMod ou Unturned interno.
- Placeholders honestos: lista de bots vazia diz "Empty = names generated automatically", nunca nomes de exemplo falsos.

## Components

Todos desenhados à mão em `Theme.cs` (WinForms, `UserPaint`), sem estilos padrão do Windows.

- **FlatButton** → `@src/Skalia.Launcher/Theme.cs#FlatButton` (`button-primary`, `button-secondary`). Desativado enquanto a ação não é possível (ex.: JOIN GAME só libera com o servidor pronto).
- **NavButton** → `@src/Skalia.Launcher/Theme.cs#NavButton` (`nav-item`). O selecionado ganha fundo `card`, texto `text` e uma barrinha vertical de 3px em `accent` à esquerda, que marca a página atual.
- **ToggleSwitch** → `@src/Skalia.Launcher/Theme.cs#ToggleSwitch` (`toggle-on`, `toggle-off`). Rótulo ao lado e dica opcional embaixo; é o controle para qualquer opção liga/desliga.
- **Combos e campos** → `Theme.StyleInput` (`input`). `Dropdown` substitui o ComboBox: fechado é fundo input, borda de 1px e chevron; aberto é uma lista escura de linhas de 28px, até 10 linhas, com a opção atual marcada por uma barrinha `muted`; `InputFrame` dá a borda de 1px às caixas de texto no lugar da borda nativa; `NumberBox` substitui o NumericUpDown (sem setinhas nativas; seta para cima/baixo muda o valor). A borda clareia para `muted` no foco, nunca para `accent`.
- **Slider** → `Slider` para multiplicadores (dificuldade dos bots): direto no card, sem fundo de campo. Trilho de 4px `input-border`, bolinha de 16px `accent`, valor ("1.25x") alinhado à direita numa coluna de 56px. Um tracinho `muted` marca o 1x, que fica no meio do trilho, e o trecho entre ele e a bolinha fica `accent`. Passos de 0.05x; setas e Home/End mudam o valor.
- **Peças do Windows** → barras de rolagem e barra de título no modo escuro do próprio Windows 10/11 (`Theme.DarkScrollbars`, `Theme.DarkTitleBar`). Nada de controle nativo claro na tela.
- **Card** → `MainForm.NewCard` (`card`). Um assunto por card, com título opcional. Em "Advanced settings" cada grupo (Bots, Bot behavior, World, Survival, Combat, Server) é um card.
- **Seção recolhível** → "Advanced settings": texto `muted` com seta, fechado por padrão.

## Do's and Don'ts

**Faça**

- Deixe o bege para o botão de jogar, os toggles ligados e o item de menu atual.
- Coloque toda opção nova primeiro em "Configurações avançadas"; ela só sobe para o card básico se a maioria dos jogadores for mexer.
- Mantenha o rótulo curto e explique o resto numa dica de uma linha, só se a dica disser algo novo.
- Teste em inglês e em português, e com o servidor parado, carregando e online.

**Não faça**

- Não use caixa alta colorida em títulos de seção.
- Não repita a mesma dica embaixo de vários campos: se vale para o grupo, ela vai uma vez no título do grupo.
- Não use gradiente, sombra, brilho, emoji ou animação em loop.
- Não mostre plugins internos, IDs técnicos ou caminhos de arquivo, a não ser no Console.
- Não crie controle que não faz nada. Se a função não existe, o controle não aparece.
