# Modelo português de controlo ferroviário — CP Virtual / Open Rails

## Objetivo

Este documento fixa o modelo operacional adotado pelo posto CP Virtual e separa os factos públicos sobre a rede portuguesa das decisões específicas da simulação Portugal79.

## Enquadramento real confirmado

A gestão da infraestrutura e o comando da circulação ferroviária nacional cabem à Infraestruturas de Portugal (IP), não à CP enquanto operador de comboios. A IP identifica três Centros de Comando e Operação (CCO): Lisboa, no Braço de Prata; Porto, em Contumil; e Setúbal. Os centros funcionam permanentemente e coordenam e supervisionam a exploração, com informação em tempo real, videovigilância, telecomando da catenária e monitorização da infraestrutura.

Fonte pública: [Infraestruturas de Portugal — Operadores de Comando Ferroviário e CCO](https://www.infraestruturasdeportugal.pt/pt-pt/recrutamento-de-30-operadores-de-comando-ferroviario-para-lisboa-e-porto).

Não foi encontrada uma fonte pública atual e suficientemente precisa que publique, estação a estação, os limites internos de cada mesa ou setor operacional. Por isso, nomes como “RSC-CT-2” e os limites Alfarelos–Coimbra-B são uma organização da CP Virtual para a rota Portugal79, e não devem ser apresentados como divisão oficial da IP.

## Princípios operacionais do painel

1. O posto mostra apenas a sua área fixa, mesmo que não existam comboios.
2. O desenho vem da via real carregada pelo Open Rails; estações, sinais, agulhas e comboios são sobrepostos pelas suas coordenadas reais.
3. Cada comboio é identificado por número e nome e mostra velocidade, circuito atual, próximo sinal e circuitos do itinerário válido.
4. O aspeto do sinal vem de `SignalObject.this_sig_lr(NORMAL)`. O campo `draw_state` é apenas o índice gráfico do modelo 3D e não representa, por si só, vermelho/verde.
5. O operador pode colocar um sinal à paragem ou devolvê-lo ao sistema. Não força diretamente um verde: o encravamento do Open Rails decide o aspeto depois de validar itinerário, ocupação, reservas e posição das agulhas.
6. Uma agulha pode receber pedido de posição 0 ou 1. O comando usa `Signals.RequestSetSwitch`, que recusa uma alteração ocupada, reservada, reivindicada ou incompatível.
7. Uma autorização para ultrapassar um sinal fechado é uma ordem separada, dirigida a um comboio e registada. Não equivale a tornar o sinal verde.
8. Em multiplayer, os comandos só são aceites pelo computador servidor. Clientes observam e comunicam pedidos.

## Correspondência com o Open Rails

O próprio mapa do Open Rails usa estas operações de segurança:

- `RequestHoldSignalDispatcher(true)`: colocar/manter o sinal fechado;
- `ClearHoldSignalDispatcher()`: devolver o sinal ao funcionamento do sistema;
- `RequestSetSwitch(trackCircuitIndex)`: pedir alteração da agulha, sujeita ao estado do encravamento.

Referências técnicas:

- [Open Rails — SignalObject](https://github.com/openrails/openrails/blob/master/Source/Orts.Simulation/Simulation/Signalling/SignalObject.cs)
- [Open Rails — comandos do mapa do dispatcher](https://github.com/openrails/openrails/blob/master/Source/RunActivity/Viewer3D/Map/MapForm.cs)
- `project_sources/02-Signalling.pdf`, incluído no material do projeto.

## Comunicações

O material de segurança fornecido ao projeto descreve o Rádio Solo-Comboio como comunicação registada entre maquinista, regulação de tráfego e estações. O painel deve, numa etapa própria, criar pedidos e respostas com hora, comboio, operador, sinal, motivo e resultado. Uma ordem de avanço perante sinal fechado só deve ser disponibilizada depois de existir identidade do comboio, validação de conflito e registo persistente.

Referência: `upload/PDF_SegurancaFerroviaria.pdf`, material fornecido ao projeto.

## Área Portugal79 inicialmente configurada

Posto simulado: **Alfarelos — Coimbra-B**

Pontos de referência configurados:

1. Alfarelos
2. Formoselha
3. Pereira do Campo
4. Amial
5. Vila Pouca do Campo
6. Taveiro
7. Casais
8. Espadaneira
9. Bencanta
10. Coimbra-B

O recorte deve ser calculado pelas posições reais desses pontos na rota. Não deve ser desenhada uma linha reta fictícia entre estações.

## Estado de implementação

Implementado:

- exportação do aspeto semântico real de cada sinal;
- coordenadas de sinais, agulhas e frente dos comboios;
- identificação, velocidade, próximo sinal e itinerário de cada comboio;
- recorte local Alfarelos–Coimbra-B sobre a via carregada;
- seleção de sinais e agulhas no quadro;
- pedidos de paragem/devolução ao sistema e de posição da agulha;
- validações nativas do Open Rails e bloqueio de comandos em cliente multiplayer.

Ainda depende de ensaio na rota:

- confirmar se todos os sinais e agulhas da Portugal79 têm coordenadas úteis;
- associar cada circuito a toda a sua polilinha para realçar o itinerário completo, não apenas a posição dos aparelhos;
- testar os comandos em Explorer, Activity e Timetable;
- implementar canal de pedidos do maquinista e autorização registada;
- definir setores adicionais apenas após validar os limites desejados para a simulação.
