# CP Virtual — multiplayer pela Internet

O Open Rails continua a ser o servidor da sessão. O CP Virtual não expõe um
segundo servidor público: cada computador abre o seu próprio painel em
`http://127.0.0.1:2150/CPVirtual/` e as mensagens Rádio Solo e os comandos do
posto seguem pela sessão multiplayer até ao anfitrião.

## No computador anfitrião

1. Reserve um IP LAN para o computador que executa o Open Rails (por exemplo,
   uma reserva DHCP no OPNsense).
2. Crie no router/firewall uma regra NAT de entrada **TCP 30000** para esse IP
   LAN e permita a mesma porta na regra WAN correspondente.
3. Autorize o executável Open Rails na Firewall do Windows para redes privadas
   e públicas, limitado à porta TCP 30000.
4. Inicie a sessão como **Server** na porta `30000`.

Não encaminhe a porta `2150`: é apenas o servidor web local do painel e deve
continuar acessível só em `127.0.0.1`.

## Nos computadores dos participantes

1. Instale a mesma compilação/versão da rota que o anfitrião.
2. Na consola multiplayer do Open Rails, escolha **Client**.
3. Indique o endereço público (ou nome DDNS) do anfitrião e a porta `30000`.
4. Abra o painel local CP Virtual. O Rádio Solo local envia mensagens ao host;
   o host valida e distribui-as de volta aos painéis da sessão.

## Verificação rápida

* O maquinista envia `LOGIN` pelo Rádio Solo.
* O registo Rádio Solo do posto anfitrião apresenta a mensagem e o número de
  serviço.
* Um pedido de sinal/agulha feito num cliente é enviado ao anfitrião; a resposta
  do anfitrião aparece no painel que originou o pedido.

Se alguém fora da rede não conseguir entrar, confirme primeiro se o endereço
WAN do firewall é igual ao endereço público visto num site de "qual é o meu
IP". Se forem diferentes, existe CGNAT ou um router intermédio e a regra de
port-forward não chega à Internet. Um hostname DDNS é recomendado mesmo quando
o IP atual costuma manter-se durante muito tempo.
