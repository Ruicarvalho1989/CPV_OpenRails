// COPYRIGHT 2026 by CP Virtual contributors.
//
// CP Virtual dispatcher export. This is an additive web API layer: it never
// changes route data or signal logic. It exposes the topology already loaded
// by Open Rails so an external dispatcher can render and observe the same
// route as the simulator.

using System;
using System.Collections.Generic;
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
    }

    /// <summary>Live state only. Request this repeatedly; topology changes only when a route is loaded.</summary>
    public sealed class CPVirtualState
    {
        public DateTime CapturedAtUtc;
        public List<CPVirtualCircuitState> Circuits = new List<CPVirtualCircuitState>();
        public List<CPVirtualSignalState> Signals = new List<CPVirtualSignalState>();
        public List<CPVirtualTrainState> Trains = new List<CPVirtualTrainState>();
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
    }

    public sealed class CPVirtualTrainState
    {
        public int Number;
        public string Name;
        public float SpeedKmh;
        public string ControlMode;
        public int FrontCircuit;
        public float FrontCircuitOffset;
    }

    public sealed class CPVirtualSignalState
    {
        public int Reference;
        public int DrawState;
        public string HoldState;
        public string Permission;
        public bool ApproachControlSet;
        public bool CallOnEnabled;
    }

    /// <summary>
    /// Reads only public ORTS simulation state. Command application deliberately
    /// belongs in a later, permission-checked CP Virtual bridge.
    /// </summary>
    public static class CPVirtualDispatcherExport
    {
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
                        }
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
                        NextSwitchCircuit = signal.nextSwitchIndex ?? -1
                    });
                }
            }

            return output;
        }

        public static CPVirtualState State(Viewer viewer)
        {
            var signals = viewer.Simulator.Signals;
            var output = new CPVirtualState { CapturedAtUtc = DateTime.UtcNow };
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
                        JunctionManualRoute = circuit.JunctionSetManual
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
                    FrontCircuitOffset = train.PresentPosition[0].TCOffset
                });
            }

            return output;
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
