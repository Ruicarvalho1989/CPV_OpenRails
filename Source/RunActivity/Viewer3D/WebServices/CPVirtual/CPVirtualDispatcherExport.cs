// COPYRIGHT 2026 by CP Virtual contributors.
//
// CP Virtual dispatcher export. This is an additive web API layer: it never
// changes route data or signal logic. It exposes the topology already loaded
// by Open Rails so an external dispatcher can render and observe the same
// route as the simulator.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Orts.Common;
using Orts.Formats.Msts;
using Orts.MultiPlayer;
using Orts.Simulation;
using Orts.Simulation.AIs;
using Orts.Simulation.Signalling;

namespace Orts.Viewer3D.WebServices
{
    /// <summary>
    /// Immutable data used to draw a dispatcher panel for the loaded route.
    /// The map comes from the existing ORTS track database export; circuit and
    /// signal identifiers provide the link to the live state endpoint.
    /// </summary>
    public sealed class CPVirtualTopology
    {
        public string RouteName;
        public string RoutePathName;
        public bool TimetableMode;
        public string TimetableFileName;
        public List<CPVirtualCircuitTopology> Circuits = new List<CPVirtualCircuitTopology>();
        public List<CPVirtualSignalTopology> Signals = new List<CPVirtualSignalTopology>();
    }

    public sealed class CPVirtualCircuitTopology
    {
        public int Index;
        public int OriginalIndex;
        public string Type;
        public float LengthMetres;
        public int JunctionDefaultRoute;
        public int[] Pins = new int[4];
        public int[] PinDirections = new int[4];
        public int[] EndSignalReferences = new int[2];
        public double? Latitude;
        public double? Longitude;
    }

    public sealed class CPVirtualSignalTopology
    {
        public int Reference;
        public int TrackNode;
        public int TrackCircuit;
        public float TrackCircuitOffset;
        public int Direction;
        public string Type;
        public int NormalHeads;
        public int NextSwitchCircuit;
        public double? Latitude;
        public double? Longitude;
    }

    /// <summary>Live state only. Request this repeatedly; topology changes only when a route is loaded.</summary>
    public sealed class CPVirtualState
    {
        public DateTime CapturedAtUtc;
        public List<CPVirtualCircuitState> Circuits = new List<CPVirtualCircuitState>();
        public List<CPVirtualSignalState> Signals = new List<CPVirtualSignalState>();
        public List<CPVirtualTrainState> Trains = new List<CPVirtualTrainState>();
        public CPVirtualCommandResult LastCommand;
    }

    public sealed class CPVirtualCircuitState
    {
        public int Index;
        public bool Occupied;
        public bool Reserved;
        public bool Claimed;
        public bool Forced;
        public bool RemoteOccupied;
        public int JunctionRoute;
        public int JunctionManualRoute;
        public bool Locked;
    }

    public sealed class CPVirtualTrainState
    {
        public int Number;
        public string Name;
        public float SpeedKmh;
        public string ControlMode;
        public int FrontCircuit;
        public float FrontCircuitOffset;
        public int FrontDirection;
        public double? Latitude;
        public double? Longitude;
        public int NextSignalReference;
        public int[] RouteCircuits = new int[0];
        public int DispatcherOrder;
        public bool DispatcherHold;
        public bool DispatcherStopAtNextStation;
        public float DispatcherSpeedLimitKmh;
    }

    public sealed class CPVirtualSignalState
    {
        public int Reference;
        public int DrawState;
        public string Aspect;
        public bool Enabled;
        public int EnabledTrainNumber;
        public string HoldState;
        public string Permission;
        public bool ApproachControlSet;
        public bool CallOnEnabled;
    }

    public sealed class CPVirtualRadioRequest
    {
        public string FromRole;
        public string FromName;
        public int TrainNumber;
        public int ServiceNumber;
        public string PostId;
        public string Channel;
        public string Group;
        public int SourceDestinationIdentifier;
        public int MessageIdentifier;
        public string Kind;
        public string Text;
    }

    public sealed class CPVirtualRadioMessage
    {
        public long MessageId;
        public DateTime SentAtUtc;
        public string FromRole;
        public string FromName;
        public int TrainNumber;
        public int ServiceNumber;
        public string PostId;
        public string Channel;
        public string Group;
        public int SourceDestinationIdentifier;
        public int MessageIdentifier;
        public int AcknowledgementCause;
        public string Kind;
        public string Text;
    }

    public sealed class CPVirtualRadioResult
    {
        public bool Accepted;
        public string Message;
        public long MessageId;
        public int AcknowledgementCause;
    }

    public sealed class CPVirtualCommand
    {
        public long CommandId;
        public string Kind;
        public int Reference;
        public int Position;
    }

    public sealed class CPVirtualCommandResult
    {
        public long CommandId;
        public bool Accepted;
        public string Message;
        public int Reference;
        public int Position;
    }

    /// <summary>
    /// Exposes the ORTS interlocking state and a narrow set of dispatcher requests.
    /// Commands reuse the simulator's own occupancy and reservation checks.
    /// </summary>
    public static class CPVirtualDispatcherExport
    {
        private sealed class CPVirtualAIOrderState
        {
            public int Order;
            public bool Hold;
            public bool StopAtNextStation;
            public float SpeedLimitMpS;
        }

        private static readonly object radioLock = new object();
        private static readonly List<CPVirtualRadioMessage> radioMessages = new List<CPVirtualRadioMessage>();
        private static long nextRadioMessageId;
        private static readonly ConcurrentQueue<CPVirtualCommand> pendingCommands = new ConcurrentQueue<CPVirtualCommand>();
        private static readonly ConcurrentQueue<CPVirtualRadioMessage> pendingAIOrders = new ConcurrentQueue<CPVirtualRadioMessage>();
        private static readonly ConcurrentDictionary<int, CPVirtualAIOrderState> aiOrders = new ConcurrentDictionary<int, CPVirtualAIOrderState>();
        private static long nextCommandId;
        private static volatile CPVirtualCommandResult lastCommand;
        private static bool multiplayerListenerAttached;
        private const string MultiplayerPrefix = "CPV1|";

        // CP Virtual is a local web panel, but an Open Rails MP client must not
        // directly alter its own simulation.  The request travels to the host
        // over the existing TEXT channel and the host broadcasts the outcome.
        // Keeping this on the established MP channel avoids changing the wire
        // protocol used by older Open Rails clients.
        private static void EnsureMultiplayerListener()
        {
            if (multiplayerListenerAttached)
                return;

            lock (radioLock)
            {
                if (multiplayerListenerAttached)
                    return;
                MPManager.Instance().MessageReceived += OnMultiplayerMessage;
                multiplayerListenerAttached = true;
            }
        }

        public static List<CPVirtualRadioMessage> RadioMessages()
        {
            EnsureMultiplayerListener();
            lock (radioLock)
                return new List<CPVirtualRadioMessage>(radioMessages);
        }

        private static bool IsKnownStatusMessage(int sourceDestination, int identifier)
        {
            if (sourceDestination == 0 && identifier == 0)
                return true; // CP Virtual free-text extension.
            if (identifier < 1 || identifier > 255)
                return false;
            if (sourceDestination == 1)
                return identifier == 1 || identifier == 2 || identifier == 3 || identifier == 4 ||
                    identifier == 5 || identifier == 6 || identifier == 7 || identifier == 8 ||
                    identifier == 14 || identifier == 15 || identifier == 16;
            if (sourceDestination == 2)
                return identifier >= 1 && identifier <= 16;
            if (sourceDestination == 3)
                return identifier >= 1 && identifier <= 10;
            if (sourceDestination == 4)
                return identifier >= 1 && identifier <= 8;
            return false;
        }

        public static CPVirtualRadioResult SendRadio(CPVirtualRadioRequest request)
        {
            EnsureMultiplayerListener();
            var result = new CPVirtualRadioResult { Accepted = false, Message = "Mensagem inválida.", AcknowledgementCause = 3 };
            if (request == null || String.IsNullOrWhiteSpace(request.FromRole) ||
                String.IsNullOrWhiteSpace(request.FromName) || String.IsNullOrWhiteSpace(request.Text))
                return result;

            if (!IsKnownStatusMessage(request.SourceDestinationIdentifier, request.MessageIdentifier))
            {
                result.Message = "Mensagem predefinida desconhecida.";
                result.AcknowledgementCause = 1;
                return result;
            }

            if (MPManager.IsClient())
            {
                MPManager.SendToServer(new MSGText(MPManager.GetUserName(), "0Server", SerializeMultiplayer("radio-request", request)).ToString());
                result.Accepted = true;
                result.Message = "Mensagem enviada ao controlador da sessão.";
                result.AcknowledgementCause = 0;
                return result;
            }

            var message = CreateRadioMessage(request);
            AddRadioMessage(message);
            QueueAIOrder(message);
            BroadcastRadio(message);

            result.Accepted = true;
            result.Message = "Mensagem transmitida.";
            result.MessageId = message.MessageId;
            result.AcknowledgementCause = 0;
            return result;
        }

        public static CPVirtualTopology Topology(Viewer viewer)
        {
            var simulator = viewer.Simulator;
            var signals = simulator.Signals;
            var output = new CPVirtualTopology
            {
                RouteName = simulator.RouteName,
                RoutePathName = simulator.RoutePathName,
                TimetableMode = simulator.TimetableMode,
                TimetableFileName = simulator.TimetableFileName
            };

            if (signals == null)
                return output;

            if (signals.TrackCircuitList != null)
            {
                foreach (var circuit in signals.TrackCircuitList)
                {
                    if (circuit == null || circuit.CircuitType == TrackCircuitSection.TrackCircuitType.Empty)
                        continue;

                    output.Circuits.Add(new CPVirtualCircuitTopology
                    {
                        Index = circuit.Index,
                        OriginalIndex = circuit.OriginalIndex,
                        Type = circuit.CircuitType.ToString(),
                        LengthMetres = circuit.Length,
                        JunctionDefaultRoute = circuit.JunctionDefaultRoute,
                        Pins = new[]
                        {
                            PinLink(circuit, 0, 0), PinLink(circuit, 0, 1),
                            PinLink(circuit, 1, 0), PinLink(circuit, 1, 1)
                        },
                        PinDirections = new[]
                        {
                            PinDirection(circuit, 0, 0), PinDirection(circuit, 0, 1),
                            PinDirection(circuit, 1, 0), PinDirection(circuit, 1, 1)
                        },
                        EndSignalReferences = new[]
                        {
                            circuit.EndSignals[0] == null ? -1 : circuit.EndSignals[0].thisRef,
                            circuit.EndSignals[1] == null ? -1 : circuit.EndSignals[1].thisRef
                        },
                        Latitude = CircuitLatitude(simulator, circuit),
                        Longitude = CircuitLongitude(simulator, circuit)
                    });
                }
            }

            if (signals.SignalObjects != null)
            {
                foreach (var signal in signals.SignalObjects)
                {
                    if (signal == null)
                        continue;

                    output.Signals.Add(new CPVirtualSignalTopology
                    {
                        Reference = signal.thisRef,
                        TrackNode = signal.trackNode,
                        TrackCircuit = signal.TCReference,
                        TrackCircuitOffset = signal.TCOffset,
                        Direction = signal.TCDirection,
                        Type = signal.Type.ToString(),
                        NormalHeads = signal.SignalNumNormalHeads,
                        NextSwitchCircuit = signal.nextSwitchIndex ?? -1,
                        Latitude = SignalLatitude(signal),
                        Longitude = SignalLongitude(signal)
                    });
                }
            }

            return output;
        }

        public static CPVirtualState State(Viewer viewer)
        {
            var signals = viewer.Simulator.Signals;
            var output = new CPVirtualState { CapturedAtUtc = DateTime.UtcNow, LastCommand = lastCommand };
            if (signals == null)
                return output;

            if (signals.TrackCircuitList != null)
            {
                foreach (var circuit in signals.TrackCircuitList)
                {
                    if (circuit == null || circuit.CircuitType == TrackCircuitSection.TrackCircuitType.Empty)
                        continue;

                    var state = circuit.CircuitState;
                    output.Circuits.Add(new CPVirtualCircuitState
                    {
                        Index = circuit.Index,
                        Occupied = state != null && (state.HasTrainsOccupying() || state.RemoteOccupied),
                        Reserved = state != null && (state.TrainReserved != null || state.SignalReserved >= 0 || state.RemoteSignalReserved),
                        Claimed = state != null && state.TrainClaimed != null && state.TrainClaimed.Count > 0,
                        Forced = state != null && state.Forced,
                        RemoteOccupied = state != null && state.RemoteOccupied,
                        JunctionRoute = circuit.JunctionLastRoute,
                        JunctionManualRoute = circuit.JunctionSetManual,
                        Locked = state != null && (state.SignalReserved >= 0 || (state.TrainClaimed != null && state.TrainClaimed.Count > 0) || state.HasTrainsOccupying())
                    });
                }
            }

            if (signals.SignalObjects != null)
            {
                foreach (var signal in signals.SignalObjects)
                {
                    if (signal == null)
                        continue;

                    output.Signals.Add(new CPVirtualSignalState
                    {
                        Reference = signal.thisRef,
                        DrawState = signal.draw_state,
                        Aspect = signal.this_sig_lr(MstsSignalFunction.NORMAL).ToString(),
                        Enabled = signal.enabled,
                        EnabledTrainNumber = signal.enabledTrain == null ? -1 : signal.enabledTrain.Train.Number,
                        HoldState = signal.holdState.ToString(),
                        Permission = signal.hasPermission.ToString(),
                        ApproachControlSet = signal.ApproachControlSet,
                        CallOnEnabled = signal.CallOnEnabled
                    });
                }
            }

            // TrainDictionary is maintained by ORTS for the active simulation,
            // including timetable services which have already been created.
            foreach (var train in viewer.Simulator.TrainDictionary.Values)
            {
                if (train == null)
                    continue;

                CPVirtualAIOrderState aiOrder;
                aiOrders.TryGetValue(train.Number, out aiOrder);
                output.Trains.Add(new CPVirtualTrainState
                {
                    Number = train.Number,
                    Name = train.Name,
                    SpeedKmh = train.SpeedMpS * 3.6f,
                    ControlMode = train.ControlMode.ToString(),
                    FrontCircuit = train.PresentPosition[0].TCSectionIndex,
                    FrontCircuitOffset = train.PresentPosition[0].TCOffset,
                    FrontDirection = train.PresentPosition[0].TCDirection,
                    Latitude = TrainLatitude(train),
                    Longitude = TrainLongitude(train),
                    NextSignalReference = train.NextSignalObject == null || train.NextSignalObject[0] == null ? -1 : train.NextSignalObject[0].thisRef,
                    RouteCircuits = TrainRouteCircuits(train),
                    DispatcherOrder = aiOrder == null ? 0 : aiOrder.Order,
                    DispatcherHold = aiOrder != null && aiOrder.Hold,
                    DispatcherStopAtNextStation = aiOrder != null && aiOrder.StopAtNextStation,
                    DispatcherSpeedLimitKmh = aiOrder == null ? 0 : aiOrder.SpeedLimitMpS * 3.6f
                });
            }

            return output;
        }

        public static CPVirtualCommandResult Queue(CPVirtualCommand command)
        {
            EnsureMultiplayerListener();
            var result = new CPVirtualCommandResult
            {
                CommandId = MPManager.IsClient() ? DateTime.UtcNow.Ticks : Interlocked.Increment(ref nextCommandId),
                Accepted = false,
                Message = "Pedido inválido.",
                Reference = command == null ? -1 : command.Reference,
                Position = command == null ? -1 : command.Position
            };

            if (command == null || String.IsNullOrWhiteSpace(command.Kind))
                return Complete(result);

            if (MPManager.IsClient())
            {
                command.CommandId = result.CommandId;
                MPManager.SendToServer(new MSGText(MPManager.GetUserName(), "0Server", SerializeMultiplayer("command-request", command)).ToString());
                result.Accepted = true;
                result.Message = "Pedido enviado ao controlador da sessão; aguarda execução.";
                return result;
            }

            pendingCommands.Enqueue(new CPVirtualCommand
            {
                CommandId = result.CommandId,
                Kind = command.Kind,
                Reference = command.Reference,
                Position = command.Position
            });

            result.Accepted = true;
            result.Message = "Pedido recebido pelo Open Rails; aguarda execução.";
            return result;
        }

        public static void ProcessPending(Viewer viewer)
        {
            // This runs once per viewer frame. It also makes the host ready to
            // accept Rádio Solo traffic even before its browser panel is opened.
            EnsureMultiplayerListener();
            CPVirtualCommand command;
            while (pendingCommands.TryDequeue(out command))
                Execute(viewer, command);

            CPVirtualRadioMessage order;
            while (pendingAIOrders.TryDequeue(out order))
                ExecuteAIOrder(viewer, order);

            ApplyAIOrders(viewer);
        }

        private static void QueueAIOrder(CPVirtualRadioMessage message)
        {
            if (message != null && message.SourceDestinationIdentifier == 3 &&
                message.MessageIdentifier >= 1 && message.MessageIdentifier <= 10)
                pendingAIOrders.Enqueue(message);
        }

        private static void ExecuteAIOrder(Viewer viewer, CPVirtualRadioMessage order)
        {
            if (viewer == null || viewer.Simulator == null || order == null || MPManager.IsClient())
                return;

            foreach (var train in viewer.Simulator.TrainDictionary.Values)
            {
                var aiTrain = train as AITrain;
                if (aiTrain == null || (train.IsActualPlayerTrain && train.IsPlayerDriven) || train.Number != order.TrainNumber)
                    continue;

                CPVirtualAIOrderState state;
                if (!aiOrders.TryGetValue(train.Number, out state))
                {
                    state = new CPVirtualAIOrderState();
                    aiOrders[train.Number] = state;
                }

                state.Order = order.MessageIdentifier;
                var executed = true;
                switch (order.MessageIdentifier)
                {
                    case 1:
                        state.Hold = true;
                        state.StopAtNextStation = false;
                        state.SpeedLimitMpS = 0;
                        break;
                    case 2:
                        state.Hold = false;
                        state.StopAtNextStation = false;
                        state.SpeedLimitMpS = 30f / 3.6f;
                        break;
                    case 3:
                        state.StopAtNextStation = true;
                        break;
                    case 4:
                        state.Hold = false;
                        state.StopAtNextStation = false;
                        state.SpeedLimitMpS = 60f / 3.6f;
                        break;
                    case 6:
                        CPVirtualAIOrderState removed;
                        aiOrders.TryRemove(train.Number, out removed);
                        aiTrain.RecalculateAllowedMaxSpeed();
                        break;
                    default:
                        executed = false;
                        break;
                }
                if (executed)
                    AcknowledgeAIOrder(train, order);
                return;
            }
        }

        private static void AcknowledgeAIOrder(Orts.Simulation.Physics.Train train, CPVirtualRadioMessage order)
        {
            var acknowledgement = CreateRadioMessage(new CPVirtualRadioRequest
            {
                FromRole = "driver-ai",
                FromName = String.IsNullOrWhiteSpace(train.Name) ? "IA " + train.Number : train.Name,
                TrainNumber = train.Number,
                ServiceNumber = order.ServiceNumber,
                PostId = order.PostId,
                Channel = order.Channel,
                Group = order.Group,
                SourceDestinationIdentifier = 1,
                MessageIdentifier = 7,
                Kind = "acknowledgement",
                Text = "CONFIRMADO — " + order.Text
            });
            AddRadioMessage(acknowledgement);
            BroadcastRadio(acknowledgement);
        }

        private static void ApplyAIOrders(Viewer viewer)
        {
            foreach (var train in viewer.Simulator.TrainDictionary.Values)
            {
                var aiTrain = train as AITrain;
                CPVirtualAIOrderState state;
                if (aiTrain == null || (train.IsActualPlayerTrain && train.IsPlayerDriven) || !aiOrders.TryGetValue(train.Number, out state))
                    continue;

                if (state.StopAtNextStation && aiTrain.MovementState == AITrain.AI_MOVEMENT_STATE.STATION_STOP)
                {
                    state.StopAtNextStation = false;
                    state.Hold = true;
                }

                if (state.SpeedLimitMpS > 0)
                    aiTrain.AllowedMaxSpeedMpS = Math.Min(aiTrain.AllowedMaxSpeedMpS, state.SpeedLimitMpS);
                if (state.Hold)
                    aiTrain.AdjustControlsBrakeFull();
            }
        }

        public static CPVirtualCommandResult Execute(Viewer viewer, CPVirtualCommand command)
        {
            var result = new CPVirtualCommandResult
            {
                CommandId = command == null ? 0 : command.CommandId,
                Accepted = false,
                Message = "Pedido inválido.",
                Reference = command == null ? -1 : command.Reference,
                Position = command == null ? -1 : command.Position
            };

            if (command == null || viewer == null || viewer.Simulator == null || viewer.Simulator.Signals == null)
                return Complete(result);

            if (MPManager.IsClient())
            {
                result.Message = "Este computador não é o servidor da sessão.";
                return Complete(result);
            }

            var signals = viewer.Simulator.Signals;
            var kind = (command.Kind ?? String.Empty).Trim().ToLowerInvariant();

            if (kind == "signal-stop" || kind == "signal-proceed" || kind == "signal-system")
            {
                SignalObject selected = null;
                foreach (var signal in signals.SignalObjects)
                {
                    if (signal != null && signal.thisRef == command.Reference)
                    {
                        selected = signal;
                        break;
                    }
                }

                if (selected == null || selected.SignalNumNormalHeads <= 0)
                {
                    result.Message = "Sinal principal não encontrado.";
                    return Complete(result);
                }

                if (kind == "signal-stop")
                    selected.RequestHoldSignalDispatcher(true);
                else if (kind == "signal-proceed")
                    selected.RequestLeastRestrictiveAspect();
                else
                    selected.ClearHoldSignalDispatcher();

                selected.Update();
                var resultingAspect = selected.this_sig_lr(MstsSignalFunction.NORMAL);
                result.Accepted =
                    kind == "signal-stop" ? selected.holdState == HoldState.ManualLock :
                    kind == "signal-proceed" ? selected.holdState == HoldState.ManualPass :
                    selected.holdState == HoldState.None;
                result.Message = result.Accepted
                    ? "Sinal executado: " + resultingAspect + " (estado " + selected.holdState + ")."
                    : "O sinal não confirmou o comando: " + resultingAspect + " (estado " + selected.holdState + ").";
                return Complete(result);
            }

            if (kind == "switch-set")
            {
                if (command.Reference < 0 || command.Reference >= signals.TrackCircuitList.Count)
                {
                    result.Message = "Agulha não encontrada.";
                    return Complete(result);
                }

                var circuit = signals.TrackCircuitList[command.Reference];
                if (circuit == null || circuit.CircuitType != TrackCircuitSection.TrackCircuitType.Junction)
                {
                    result.Message = "O circuito indicado não é uma agulha.";
                    return Complete(result);
                }

                if (command.Position != 0 && command.Position != 1)
                {
                    result.Message = "Posição de agulha inválida.";
                    return Complete(result);
                }

                if (circuit.JunctionLastRoute == command.Position)
                {
                    result.Accepted = true;
                    result.Message = "A agulha já se encontra nessa posição.";
                    return Complete(result);
                }

                if (circuit.OriginalIndex < 0 || circuit.OriginalIndex >= viewer.Simulator.TDB.TrackDB.TrackNodes.Length)
                {
                    result.Message = "Não foi possível localizar a agulha na via.";
                    return Complete(result);
                }

                var switchNode = viewer.Simulator.TDB.TrackDB.TrackNodes[circuit.OriginalIndex];
                if (switchNode == null || switchNode.TCCrossReference == null || switchNode.TCCrossReference.Count == 0)
                {
                    result.Message = "A agulha não possui referência de via válida.";
                    return Complete(result);
                }

                result.Accepted = signals.RequestSetSwitch(switchNode, command.Position);
                result.Position = circuit.JunctionLastRoute;
                result.Message = result.Accepted
                    ? "Agulha colocada na posição " + result.Position + "."
                    : "Comando recusado: agulha ocupada ou reservada.";
                return Complete(result);
            }

            result.Message = "Tipo de comando desconhecido.";
            return Complete(result);
        }

        private static CPVirtualCommandResult Complete(CPVirtualCommandResult result)
        {
            lastCommand = result;
            if (MPManager.IsServer())
                BroadcastToClients("command-result", result);
            return result;
        }

        private static CPVirtualRadioMessage CreateRadioMessage(CPVirtualRadioRequest request)
        {
            return new CPVirtualRadioMessage
            {
                MessageId = Interlocked.Increment(ref nextRadioMessageId),
                SentAtUtc = DateTime.UtcNow,
                FromRole = request.FromRole.Trim(),
                FromName = request.FromName.Trim(),
                TrainNumber = request.TrainNumber,
                ServiceNumber = request.ServiceNumber,
                PostId = request.PostId == null ? String.Empty : request.PostId.Trim(),
                Channel = request.Channel == null ? String.Empty : request.Channel.Trim(),
                Group = String.IsNullOrWhiteSpace(request.Group) ? "GR" : request.Group.Trim(),
                SourceDestinationIdentifier = request.SourceDestinationIdentifier,
                MessageIdentifier = request.MessageIdentifier,
                AcknowledgementCause = 0,
                Kind = String.IsNullOrWhiteSpace(request.Kind) ? "radio" : request.Kind.Trim(),
                Text = request.Text.Trim()
            };
        }

        private static void AddRadioMessage(CPVirtualRadioMessage message)
        {
            if (message == null)
                return;
            lock (radioLock)
            {
                radioMessages.Add(message);
                if (radioMessages.Count > 100)
                    radioMessages.RemoveRange(0, radioMessages.Count - 100);
            }
        }

        private static string SerializeMultiplayer(string kind, object value)
        {
            var json = JsonConvert.SerializeObject(value);
            return MultiplayerPrefix + kind + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        private static bool TryReadMultiplayer(string text, out string kind, out string json)
        {
            kind = null;
            json = null;
            var marker = text == null ? -1 : text.IndexOf(MultiplayerPrefix, StringComparison.Ordinal);
            if (marker < 0)
                return false;
            var payload = text.Substring(marker + MultiplayerPrefix.Length);
            var separator = payload.IndexOf('|');
            if (separator <= 0 || separator == payload.Length - 1)
                return false;
            try
            {
                kind = payload.Substring(0, separator);
                json = Encoding.UTF8.GetString(Convert.FromBase64String(payload.Substring(separator + 1).Trim()));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void OnMultiplayerMessage(object sender, MPManager.MessageReceivedEventArgs e)
        {
            string kind;
            string json;
            if (e == null || !TryReadMultiplayer(e.Message, out kind, out json))
                return;

            try
            {
                if (kind == "radio-request" && MPManager.IsServer())
                {
                    var request = JsonConvert.DeserializeObject<CPVirtualRadioRequest>(json);
                    if (request == null || !IsKnownStatusMessage(request.SourceDestinationIdentifier, request.MessageIdentifier))
                        return;
                    var message = CreateRadioMessage(request);
                    AddRadioMessage(message);
                    QueueAIOrder(message);
                    BroadcastRadio(message);
                }
                else if (kind == "radio-broadcast")
                {
                    AddRadioMessage(JsonConvert.DeserializeObject<CPVirtualRadioMessage>(json));
                }
                else if (kind == "command-request" && MPManager.IsServer())
                {
                    var command = JsonConvert.DeserializeObject<CPVirtualCommand>(json);
                    if (command != null && !String.IsNullOrWhiteSpace(command.Kind))
                        pendingCommands.Enqueue(command);
                }
                else if (kind == "command-result")
                {
                    lastCommand = JsonConvert.DeserializeObject<CPVirtualCommandResult>(json);
                }
            }
            catch (JsonException)
            {
                // A malformed peer message must never interrupt the simulator.
            }
        }

        private static void BroadcastRadio(CPVirtualRadioMessage message)
        {
            if (MPManager.IsServer())
                BroadcastToClients("radio-broadcast", message);
        }

        private static void BroadcastToClients(string kind, object value)
        {
            if (!MPManager.IsServer())
                return;
            var recipients = new List<string>(MPManager.OnlineTrains.Players.Keys);
            if (recipients.Count == 0)
                return;
            MPManager.BroadCast(new MSGText(MPManager.GetUserName(), String.Join("\r", recipients), SerializeMultiplayer(kind, value)).ToString());
        }

        private static double? CircuitLatitude(Simulator simulator, TrackCircuitSection circuit)
        {
            var point = CircuitLocation(simulator, circuit);
            return point == null ? (double?)null : point.Lat;
        }

        private static double? CircuitLongitude(Simulator simulator, TrackCircuitSection circuit)
        {
            var point = CircuitLocation(simulator, circuit);
            return point == null ? (double?)null : point.Lon;
        }

        private static LatLon CircuitLocation(Simulator simulator, TrackCircuitSection circuit)
        {
            if (circuit.CircuitType != TrackCircuitSection.TrackCircuitType.Junction ||
                circuit.OriginalIndex < 0 || circuit.OriginalIndex >= simulator.TDB.TrackDB.TrackNodes.Length)
                return null;

            var node = simulator.TDB.TrackDB.TrackNodes[circuit.OriginalIndex];
            if (node == null || node.UiD == null)
                return null;

            return InfoApiMap.ConvertToLatLon(node.UiD.TileX, node.UiD.TileZ, node.UiD.X, node.UiD.Y, node.UiD.Z);
        }

        private static double? SignalLatitude(SignalObject signal)
        {
            var point = SignalLocation(signal);
            return point == null ? (double?)null : point.Lat;
        }

        private static double? SignalLongitude(SignalObject signal)
        {
            var point = SignalLocation(signal);
            return point == null ? (double?)null : point.Lon;
        }

        private static LatLon SignalLocation(SignalObject signal)
        {
            // thisRef indexes SignalObjects, not the TDB track items. A signal can
            // contain several heads; the first head provides a stable TDB location.
            if (SignalObject.trItems == null || signal.SignalHeads == null || signal.SignalHeads.Count == 0)
                return null;

            var trackItemIndex = signal.SignalHeads[0].TDBIndex;
            if (trackItemIndex < 0 || trackItemIndex >= SignalObject.trItems.Length)
                return null;

            var item = SignalObject.trItems[trackItemIndex];
            return item == null ? null : InfoApiMap.ConvertToLatLon(item.TileX, item.TileZ, item.X, item.Y, item.Z);
        }

        private static double? TrainLatitude(Orts.Simulation.Physics.Train train)
        {
            var point = TrainLocation(train);
            return point == null ? (double?)null : point.Lat;
        }

        private static double? TrainLongitude(Orts.Simulation.Physics.Train train)
        {
            var point = TrainLocation(train);
            return point == null ? (double?)null : point.Lon;
        }

        private static LatLon TrainLocation(Orts.Simulation.Physics.Train train)
        {
            if (train.FrontTDBTraveller == null)
                return null;

            var traveller = train.FrontTDBTraveller;
            return InfoApiMap.ConvertToLatLon(traveller.TileX, traveller.TileZ,
                traveller.Location.X, traveller.Location.Y, traveller.Location.Z);
        }

        private static int[] TrainRouteCircuits(Orts.Simulation.Physics.Train train)
        {
            if (train.ValidRoute == null || train.ValidRoute[0] == null)
                return new int[0];

            var route = new int[train.ValidRoute[0].Count];
            for (var index = 0; index < route.Length; index++)
                route[index] = train.ValidRoute[0][index].TCSectionIndex;
            return route;
        }

        private static int PinLink(TrackCircuitSection circuit, int direction, int pin)
        {
            return circuit.Pins[direction, pin] == null ? -1 : circuit.Pins[direction, pin].Link;
        }

        private static int PinDirection(TrackCircuitSection circuit, int direction, int pin)
        {
            return circuit.Pins[direction, pin] == null ? -1 : circuit.Pins[direction, pin].Direction;
        }
    }
}
