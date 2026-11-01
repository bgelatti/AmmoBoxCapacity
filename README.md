# Ammo Box Capacity 1.0.0

Mod BepInEx 5 para **Apocalypter** que deixa você escolher quanta munição cabe nas **caixas de munição grandes** originais do jogo (`ammo_box_big_*`), com **limite rígido** por calibre.

## Como o jogo funciona sem o mod

- Uma caixa grande só aceita outra caixa (segurando uma caixa na mão e apertando **F**, "Transfer Ammo") enquanto tem **até 499** munições.
- Quando aceita, ela recebe **a caixa da mão inteira**, sem cortar nada. Por isso algumas caixas ficam com um pouco mais de 500 (506, 548, 654...).
- Com 500 ou mais, a caixa fica "cheia" e não aceita mais nada até baixar de 500.
- O limite é **500 para todos os calibres** e vem das próprias caixas (FSM `AmmoStorage`). O mod lê esse valor do jogo enquanto ele roda.

## O que o mod faz

- Troca o limite de 500 das caixas grandes pelo valor que você escolher.
- **Limite rígido**: ao juntar caixas, só passa munição até o limite. **O que sobrar fica na caixa da mão**, que não some. Se a caixa da mão esvaziar, ela some como no jogo normal.
- Caixas que já estão acima de 500 (por exemplo 548) **voltam a aceitar munição** até o novo limite.
- Funciona nas caixas que já existem e nas que aparecerem depois (verificação leve a cada 2 segundos).
- **Não mexe** em:
  - caixas pequenas (`ammo_box_22`, `ammo_box_9mm`, etc.);
  - capacidade de munição do jogador (isso é do mod *Configurable Capacity*);
  - conteúdo inicial das caixas encontradas no mundo;
  - caixas que já estão acima do novo limite: elas não perdem munição, só não aceitam mais.

## Instalação

1. Precisa do **BepInEx 5** instalado no jogo.
2. Baixe o `AmmoBoxCapacity-<versão>.zip` em [Releases](https://github.com/bgelatti/AmmoBoxCapacity/releases) e extraia a pasta `AmmoBoxCapacity` para `Apocalypter\BepInEx\plugins\`.
3. Abra o jogo uma vez para criar o arquivo de configuração.

## Configuração

Arquivo `BepInEx\config\com.gelatti.apocalypter.ammoboxcapacity.cfg` (também aparece no **Apocasetter**):

| Opção | Padrão | O que faz |
|---|---|---|
| `[General] Enabled` | `true` | Desligado = limite original do jogo (500). |
| `[General] Multiplier` | `1` | Multiplica o limite original (500) nos calibres que estão com 0. Ex.: `4` = 2000. |
| `[General] MaxPerBox_22` | `0` | Número exato para a caixa grande de .22. `0` = original x Multiplier. |
| `[General] MaxPerBox_9mm` | `0` | Idem para 9mm. |
| `[General] MaxPerBox_556mm` | `0` | Idem para 5.56mm. |
| `[General] MaxPerBox_762mm` | `0` | Idem para 7.62mm. |
| `[General] MaxPerBox_3006` | `0` | Idem para .30-06. |
| `[General] MaxPerBox_12gauge` | `0` | Idem para 12 gauge. |
| `[General] MaxPerBox_20gauge` | `0` | Idem para 20 gauge. |
| `[General] MaxPerBox_arrow` | `0` | Idem para flechas. |

Exemplo: `MaxPerBox_556mm = 3000` faz a caixa grande de 5.56 guardar até 3000, e os outros calibres seguem o `Multiplier`.

As mudanças valem na hora, sem reiniciar. Nas caixas vistas até hoje só existem versões grandes de 12 gauge, 5.56, 7.62 e .30-06. As outras entradas só têm efeito se o jogo tiver caixa grande desse calibre.

No `LogOutput.log` o mod mostra, ao iniciar, a configuração de cada calibre. Ao encontrar a primeira caixa de cada calibre, mostra o **limite original lido do jogo** e o **novo limite**, por exemplo `[box] 556mm: original limit 500, new limit 3000`.

## Aviso: faça backup do save

A munição das caixas é salva junto com o jogo. **Se você remover o mod**, caixas acima de 500 **não perdem munição**, mas ficam "cheias" e não aceitam mais nada até baixarem de 500. Mesmo assim, **faça backup do save** antes de instalar ou remover o mod.

## Compilar

```
dotnet build -c Release
```

O build Release monta `release\AmmoBoxCapacity\` com a DLL, este README, o LICENSE e o CHANGELOG, pronta para copiar para `BepInEx\plugins`. Use `-p:GamePath="..."` se o jogo estiver em outra pasta. Com `-p:CopyToGame=true` a DLL e o README também são copiados direto para `BepInEx\plugins\AmmoBoxCapacity`.

### Build de testes (Debug)

```
dotnet build -c Debug -p:CopyToGame=true
```

O build Debug inclui as ferramentas de diagnóstico (`src/Diagnostics.cs`), que **não entram no Release**. Elas ficam na seção `[Debug]` da config:

- **F9** (`DumpKey`): olhando para uma caixa, grava no log todas as FSMs dela e passa a registrar as mudanças das variáveis.
- **F10** (`DumpGameManagerKey`): grava as FSMs do `__GameManager__`.
- `LogActions` (desligado): registra ações int/float das FSMs de munição. Só vale depois de reiniciar o jogo e deixa o jogo um pouco mais lento.

O Debug também registra cada transferência entre caixas, as caixas pequenas ignoradas e os limites novos a cada mudança na config.
