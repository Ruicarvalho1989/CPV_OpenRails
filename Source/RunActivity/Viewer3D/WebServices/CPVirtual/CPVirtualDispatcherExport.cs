// COPYRIGHT 2026 by CP Virtual contributors.
//
// CP Virtual dispatcher export. This is an additive web API layer: it never
// changes route data or signal logic. It exposes the topology already loaded
// by Open Rails so an external dispatcher can render and observe the same
// route as the simulator.

using System;
using System.Collections.Generic;
using Orts.Common;
using Orts.Formats.Msts;
using Orts.MultiPlayer;
using Orts.Simulation;
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

    public sealed class CPVirtualCommand
    {
        public string Kind;
        public int Reference;
        public int Position;
    }

    public sealed class CPVirtualCommandResult
    {
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
        private static CPVirtualCommandResult lastCommand;
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
                    RouteCircuits = TrainRouteCircuits(train)
                });
            }

            return output;
        }

        public static CPVirtualCommandResult Execute(Viewer viewer, CPVirtualCommand command)
        {
            var result = new CPVirtualCommandResult
            {
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

            if (kind == "signal-stop" || kind == "signal-system")
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
                else
                    selected.ClearHoldSignalDispatcher();

                result.Accepted = true;
                result.Message = kind == "signal-stop"
                    ? "Sinal colocado sob comando de paragem (estado " + selected.holdState + ")."
                    : "Sinal devolvido ao sistema de encravamento (estado " + selected.holdState + ").";
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

                result.Accepted = signals.RequestSetSwitch(command.Reference);
                result.Position = circuit.JunctionLastRoute;
                result.Message = result.Accepted
                    ? "Agulha comandada."
                    : "Comando recusado: agulha ocupada, reservada ou encravada.";
                return Complete(result);
            }

            result.Message = "Tipo de comando desconhecido.";
            return Complete(result);
        }

        private static CPVirtualCommandResult Complete(CPVirtualCommandResult result)
        {
            lastCommand = result;
            return result;
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
