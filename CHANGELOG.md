# Changelog

## 1.0.0
- Limite configurável para as caixas de munição grandes (`ammo_box_big_*`): `MaxPerBox_<calibre>` com um número exato, ou `Multiplier` sobre o limite original (500), que é lido das caixas enquanto o jogo roda.
- Limite rígido: ao juntar caixas, a munição só passa até o limite, e o que sobrar fica na caixa da mão.
- Caixas que já estão acima de 500 voltam a aceitar munição até o novo limite.
- Vale para as caixas já existentes e para as que aparecem depois (verificação a cada 2 s); as mudanças na config valem na hora.
- Não mexe em caixas pequenas, na capacidade de munição do jogador nem no conteúdo inicial das caixas.
- Ferramentas de diagnóstico (F9, F10, `LogActions`) só no build Debug; o Release não as inclui.
- Log enxuto: no Release só a configuração ao iniciar, o limite original e o novo de cada calibre, avisos e erros.

## 0.1.0
- Build de diagnóstico, usada para descobrir onde o jogo aplica o limite das caixas.
