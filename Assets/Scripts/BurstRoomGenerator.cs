using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Burst;
using UnityEngine;

public class BurstRoomGenerator : MonoBehaviour
{
    [Header("3D Noise Settings")]
    [SerializeField] private Vector3 noiseScale = new Vector3(0.03f, 0.03f, 0.03f);
    [SerializeField] private bool useNoiseCache = false;
    
    [Header("Room Detection")]
    [SerializeField] private float roomThreshold = 0.65f;
    [SerializeField] private float roomExpansionThreshold = 0.55f;
    [SerializeField] private int minRoomSize = 4;
    [SerializeField] private int maxRoomSize = 30;
    [SerializeField] private int roomScanStep = 4;
    
    [Header("Room Expansion")]
    [SerializeField] private int maxExpansionSteps = 20;
    [SerializeField] private int gridAlignment = 2;
    
    [Header("Corridor Settings")]
    [SerializeField] private int minCorridorWidth = 3;
    [SerializeField] private int maxCorridorWidth = 5;
    [SerializeField] private int corridorHeight = 4;
    [SerializeField] private int verticalShaftSize = 3;
    [SerializeField] private float verticalConnectionChance = 0.3f;
    
    [Header("Performance")]
    [SerializeField] private int maxRoomsPerChunk = 10;
    [SerializeField] private int maxCorridorsPerChunk = 5;
    
    // Native collections
    private NativeList<RoomData> allRooms;
    private NativeList<CorridorData> allCorridors;
    private NativeParallelMultiHashMap<int3, int> chunkToRooms;
    private NativeParallelMultiHashMap<int3, int> chunkToCorridors;
    private NativeParallelMultiHashMap<int3, int> roomSpatialGrid;
    private NativeHashSet<int3> processedChunks;
    
    // State
    private int nextRoomId = 0;
    private int nextCorridorId = 0;
    private int3 currentChunkSize;
    private int worldSeed;
    
    // Job handles
    private JobHandle currentJobHandle;
    
    // Structs
    public struct RoomData
    {
        public int id;
        public int3 minBounds;
        public int3 maxBounds;
        public int3 generationChunk;
        public int isActive;
        
        public int3 Center => minBounds + (maxBounds - minBounds) / 2;
        public int3 Size => maxBounds - minBounds + new int3(1, 1, 1);
        
        public bool ContainsPoint(int3 worldPos)
        {
            return worldPos.x >= minBounds.x && worldPos.x <= maxBounds.x &&
                   worldPos.y >= minBounds.y && worldPos.y <= maxBounds.y &&
                   worldPos.z >= minBounds.z && worldPos.z <= maxBounds.z;
        }
        
        public bool Overlaps(RoomData other)
        {
            return !(maxBounds.x < other.minBounds.x || minBounds.x > other.maxBounds.x ||
                     maxBounds.y < other.minBounds.y || minBounds.y > other.maxBounds.y ||
                     maxBounds.z < other.minBounds.z || minBounds.z > other.maxBounds.z);
        }
    }
    
    public struct CorridorData
    {
        public int id;
        public int roomAId;
        public int roomBId;
        public int isVertical;
        public int3 start;
        public int3 end;
        public int width;
        public int height;
        public int isActive;
    }
    
    void Awake()
    {
        allRooms = new NativeList<RoomData>(1000, Allocator.Persistent);
        allCorridors = new NativeList<CorridorData>(500, Allocator.Persistent);
        chunkToRooms = new NativeParallelMultiHashMap<int3, int>(5000, Allocator.Persistent);
        chunkToCorridors = new NativeParallelMultiHashMap<int3, int>(5000, Allocator.Persistent);
        roomSpatialGrid = new NativeParallelMultiHashMap<int3, int>(5000, Allocator.Persistent);
        processedChunks = new NativeHashSet<int3>(1000, Allocator.Persistent);
    }
    
    void OnDestroy()
    {
        currentJobHandle.Complete();
        
        allRooms.Dispose();
        allCorridors.Dispose();
        chunkToRooms.Dispose();
        chunkToCorridors.Dispose();
        roomSpatialGrid.Dispose();
        processedChunks.Dispose();
    }
    
    public void Initialize(int3 chunkSize, int seed)
    {
        currentJobHandle.Complete();
        
        currentChunkSize = chunkSize;
        worldSeed = seed;
        
        allRooms.Clear();
        allCorridors.Clear();
        chunkToRooms.Clear();
        chunkToCorridors.Clear();
        roomSpatialGrid.Clear();
        processedChunks.Clear();
        
        nextRoomId = 0;
        nextCorridorId = 0;
    }
    
    public JobHandle ScheduleChunkGeneration(int3 chunkCoord, NativeArray<byte> outputGrid, JobHandle dependency = default)
    {
        currentJobHandle.Complete();
        
        // Create temp collections
        var localRooms = new NativeList<RoomData>(maxRoomsPerChunk, Allocator.TempJob);
        var localCorridors = new NativeList<CorridorData>(maxCorridorsPerChunk, Allocator.TempJob);
        var localProcessedPoints = new NativeHashSet<int3>(100, Allocator.TempJob);
        var newRooms = new NativeList<RoomData>(maxRoomsPerChunk, Allocator.TempJob);
        var newCorridors = new NativeList<CorridorData>(maxCorridorsPerChunk, Allocator.TempJob);
        
        var job = new GenerateChunkJob
        {
            chunkCoord = chunkCoord,
            chunkSize = currentChunkSize,
            worldSeed = worldSeed,
            noiseScale = new float3(noiseScale.x, noiseScale.y, noiseScale.z),
            
            // Settings
            roomThreshold = roomThreshold,
            roomExpansionThreshold = roomExpansionThreshold,
            minRoomSize = minRoomSize,
            maxRoomSize = maxRoomSize,
            roomScanStep = roomScanStep,
            maxExpansionSteps = maxExpansionSteps,
            gridAlignment = gridAlignment,
            minCorridorWidth = minCorridorWidth,
            maxCorridorWidth = maxCorridorWidth,
            corridorHeight = corridorHeight,
            verticalShaftSize = verticalShaftSize,
            verticalConnectionChance = verticalConnectionChance,
            maxRoomsPerChunk = maxRoomsPerChunk,
            maxCorridorsPerChunk = maxCorridorsPerChunk,
            
            // Persistent data
            allRooms = allRooms,
            allCorridors = allCorridors,
            chunkToRooms = chunkToRooms,
            chunkToCorridors = chunkToCorridors,
            roomSpatialGrid = roomSpatialGrid,
            processedChunks = processedChunks,
            
            // Local collections
            localRooms = localRooms,
            localCorridors = localCorridors,
            localProcessedPoints = localProcessedPoints,
            
            // Output collections
            newRooms = newRooms,
            newCorridors = newCorridors,
            
            // Final output
            outputGrid = outputGrid,
            nextRoomId = nextRoomId,
            nextCorridorId = nextCorridorId
        };
        
        currentJobHandle = job.Schedule(dependency);
        
        // Merge job
        var mergeJob = new MergeResultsJob
        {
            allRooms = allRooms,
            allCorridors = allCorridors,
            chunkToRooms = chunkToRooms,
            chunkToCorridors = chunkToCorridors,
            roomSpatialGrid = roomSpatialGrid,
            processedChunks = processedChunks,
            newRooms = newRooms,
            newCorridors = newCorridors,
            chunkCoord = chunkCoord,
            chunkSize = currentChunkSize
        };
        
        currentJobHandle = mergeJob.Schedule(currentJobHandle);
        
        // Dispose temp collections
        JobHandle disposeHandle = JobHandle.CombineDependencies(
            localRooms.Dispose(currentJobHandle),
            localCorridors.Dispose(currentJobHandle)
        );
        
        disposeHandle = JobHandle.CombineDependencies(
            disposeHandle,
            localProcessedPoints.Dispose(currentJobHandle)
        );
        
        disposeHandle = JobHandle.CombineDependencies(
            disposeHandle,
            newRooms.Dispose(currentJobHandle)
        );
        
        disposeHandle = JobHandle.CombineDependencies(
            disposeHandle,
            newCorridors.Dispose(currentJobHandle)
        );
        
        currentJobHandle = JobHandle.CombineDependencies(currentJobHandle, disposeHandle);
        
        return currentJobHandle;
    }
    
    public void CompleteJobs()
    {
        currentJobHandle.Complete();
    }
    
    [BurstCompile]
    private struct GenerateChunkJob : IJob
    {
        // Input parameters
        public int3 chunkCoord;
        public int3 chunkSize;
        public int worldSeed;
        public float3 noiseScale;
        
        public float roomThreshold;
        public float roomExpansionThreshold;
        public int minRoomSize;
        public int maxRoomSize;
        public int roomScanStep;
        public int maxExpansionSteps;
        public int gridAlignment;
        
        public int minCorridorWidth;
        public int maxCorridorWidth;
        public int corridorHeight;
        public int verticalShaftSize;
        public float verticalConnectionChance;
        public int maxRoomsPerChunk;
        public int maxCorridorsPerChunk;
        
        // Persistent data
        [ReadOnly] public NativeList<RoomData> allRooms;
        [ReadOnly] public NativeList<CorridorData> allCorridors;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> chunkToRooms;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> chunkToCorridors;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> roomSpatialGrid;
        [ReadOnly] public NativeHashSet<int3> processedChunks;
        
        // Local collections
        public NativeList<RoomData> localRooms;
        public NativeList<CorridorData> localCorridors;
        public NativeHashSet<int3> localProcessedPoints;
        
        // Output collections
        public NativeList<RoomData> newRooms;
        public NativeList<CorridorData> newCorridors;
        
        // Final output
        public NativeArray<byte> outputGrid;
        public int nextRoomId;
        public int nextCorridorId;
        
        // Local state
        private FastRandom random;
        
        public void Execute()
        {
            // Clear output grid
            int voxelCount = chunkSize.x * chunkSize.y * chunkSize.z;
            for (int i = 0; i < voxelCount; i++)
            {
                outputGrid[i] = 0;
            }
            
            // Clear local collections
            localRooms.Clear();
            localCorridors.Clear();
            localProcessedPoints.Clear();
            newRooms.Clear();
            newCorridors.Clear();
            
            // Initialize random
            random = new FastRandom(GetChunkSeed(chunkCoord, worldSeed));
            
            // Generate rooms if chunk not processed
            if (!processedChunks.Contains(chunkCoord))
            {
                GenerateRoomsForChunk();
                if (localRooms.Length > 1)
                {
                    ConnectRooms();
                }
            }
            
            // Carve everything to grid
            CarveRoomsToGrid();
            CarveCorridorsToGrid();
            
            // Copy to output
            CopyResults();
        }
        
        private void GenerateRoomsForChunk()
        {
            int3 worldOffset = chunkCoord * chunkSize;
            
            for (int x = 0; x < chunkSize.x; x += roomScanStep)
            {
                for (int y = 0; y < chunkSize.y; y += roomScanStep)
                {
                    for (int z = 0; z < chunkSize.z; z += roomScanStep)
                    {
                        if (localRooms.Length >= maxRoomsPerChunk) return;
                        
                        int3 worldPos = new int3(x, y, z) + worldOffset;
                        
                        float noiseValue = GetNoise(worldPos.x, worldPos.y, worldPos.z, worldSeed, noiseScale);
                        
                        if (noiseValue > roomThreshold && !IsPointInAnyRoom(worldPos))
                        {
                            CreateAndExpandRoom(worldPos);
                        }
                    }
                }
            }
        }
        
        private void CreateAndExpandRoom(int3 seedPos)
        {
            RoomData room = new RoomData
            {
                id = nextRoomId++,
                minBounds = seedPos,
                maxBounds = seedPos,
                generationChunk = chunkCoord,
                isActive = 1
            };
            
            ExpandRoomGeometrically(ref room);
            
            if (room.Size.x >= minRoomSize && room.Size.y >= minRoomSize && room.Size.z >= minRoomSize)
            {
                localRooms.Add(room);
            }
            else
            {
                nextRoomId--;
            }
        }
        
        // ORIGINAL ROOM EXPANSION ALGORITHM
        private void ExpandRoomGeometrically(ref RoomData room)
        {
            int3 currentMin = room.minBounds;
            int3 currentMax = room.maxBounds;
            NativeArray<bool> expansionBlocked = new NativeArray<bool>(6, Allocator.Temp);
            
            // Start with all directions
            NativeList<int> directionOrder = new NativeList<int>(6, Allocator.Temp);
            for (int i = 0; i < 6; i++) directionOrder.Add(i);
            ShuffleDirections(ref directionOrder);
            
            for (int step = 0; step < maxExpansionSteps; step++)
            {
                bool expanded = false;
                
                // Filter out blocked directions at the start of each step
                NativeList<int> activeDirections = new NativeList<int>(Allocator.Temp);
                for (int i = 0; i < directionOrder.Length; i++)
                {
                    if (!expansionBlocked[directionOrder[i]])
                        activeDirections.Add(directionOrder[i]);
                }
                
                if (activeDirections.Length == 0)
                    break; // All directions are blocked
                    
                for (int i = 0; i < activeDirections.Length; i++)
                {
                    int dirIndex = activeDirections[i];
                    int3 direction = IndexToDirection(dirIndex);
                    
                    if (TryExpandDirection(ref currentMin, ref currentMax, direction, room.id))
                    {
                        expanded = true;
                        // Reshuffle only active directions after successful expansion
                        ShuffleDirections(ref activeDirections);
                    }
                    else
                    {
                        expansionBlocked[dirIndex] = true;
                    }
                }
                
                // Update directionOrder with new active directions for next iteration
                directionOrder.Clear();
                for (int i = 0; i < activeDirections.Length; i++)
                {
                    directionOrder.Add(activeDirections[i]);
                }
                
                activeDirections.Dispose();
                
                if (!expanded || RoomExceedsMaxSize(currentMin, currentMax))
                    break;
            }
            
            room.minBounds = currentMin;
            room.maxBounds = currentMax;
            
            expansionBlocked.Dispose();
            directionOrder.Dispose();
        }
        
        private bool TryExpandDirection(ref int3 currentMin, ref int3 currentMax, int3 direction, int roomId)
        {
            int3 expandMin, expandMax;
            
            if (direction.x == -1)
            {
                expandMin = new int3(currentMin.x - gridAlignment, currentMin.y, currentMin.z);
                expandMax = new int3(currentMin.x - 1, currentMax.y, currentMax.z);
            }
            else if (direction.x == 1)
            {
                expandMin = new int3(currentMax.x + 1, currentMin.y, currentMin.z);
                expandMax = new int3(currentMax.x + gridAlignment, currentMax.y, currentMax.z);
            }
            else if (direction.y == -1)
            {
                expandMin = new int3(currentMin.x, currentMin.y - gridAlignment, currentMin.z);
                expandMax = new int3(currentMax.x, currentMin.y - 1, currentMax.z);
            }
            else if (direction.y == 1)
            {
                expandMin = new int3(currentMin.x, currentMax.y + 1, currentMin.z);
                expandMax = new int3(currentMax.x, currentMax.y + gridAlignment, currentMax.z);
            }
            else if (direction.z == -1)
            {
                expandMin = new int3(currentMin.x, currentMin.y, currentMin.z - gridAlignment);
                expandMax = new int3(currentMax.x, currentMax.y, currentMin.z - 1);
            }
            else // forward
            {
                expandMin = new int3(currentMin.x, currentMin.y, currentMax.z + 1);
                expandMax = new int3(currentMax.x, currentMax.y, currentMax.z + gridAlignment);
            }
            
            if (WouldOverlapOtherRooms(expandMin, expandMax, roomId))
                return false;
            
            int validSamples = 0;
            int totalSamples = 0;
            
            for (int x = expandMin.x; x <= expandMax.x; x += gridAlignment)
            {
                for (int y = expandMin.y; y <= expandMax.y; y += gridAlignment)
                {
                    for (int z = expandMin.z; z <= expandMax.z; z += gridAlignment)
                    {
                        totalSamples++;
                        float noiseValue = GetNoise(x, y, z, worldSeed, noiseScale);
                        
                        if (noiseValue > roomExpansionThreshold)
                        {
                            validSamples++;
                        }
                    }
                }
            }
            
            if (totalSamples > 0 && (float)validSamples / totalSamples >= 0.6f)
            {
                if (direction.x == -1) currentMin.x = expandMin.x;
                else if (direction.x == 1) currentMax.x = expandMax.x;
                else if (direction.y == -1) currentMin.y = expandMin.y;
                else if (direction.y == 1) currentMax.y = expandMax.y;
                else if (direction.z == -1) currentMin.z = expandMin.z;
                else currentMax.z = expandMax.z;
                
                return true;
            }
            
            return false;
        }
        
        private bool WouldOverlapOtherRooms(int3 min, int3 max, int excludeRoomId)
        {
            // Check against persistent rooms
            for (int i = 0; i < allRooms.Length; i++)
            {
                var otherRoom = allRooms[i];
                if (otherRoom.isActive == 1 && otherRoom.id != excludeRoomId)
                {
                    if (!(max.x < otherRoom.minBounds.x || min.x > otherRoom.maxBounds.x ||
                          max.y < otherRoom.minBounds.y || min.y > otherRoom.maxBounds.y ||
                          max.z < otherRoom.minBounds.z || min.z > otherRoom.maxBounds.z))
                    {
                        return true;
                    }
                }
            }
            
            // Check against local rooms
            for (int i = 0; i < localRooms.Length; i++)
            {
                var otherRoom = localRooms[i];
                if (otherRoom.id != excludeRoomId)
                {
                    if (!(max.x < otherRoom.minBounds.x || min.x > otherRoom.maxBounds.x ||
                          max.y < otherRoom.minBounds.y || min.y > otherRoom.maxBounds.y ||
                          max.z < otherRoom.minBounds.z || min.z > otherRoom.maxBounds.z))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        private bool RoomExceedsMaxSize(int3 min, int3 max)
        {
            int3 size = max - min + new int3(1, 1, 1);
            return size.x > maxRoomSize || size.y > maxRoomSize || size.z > maxRoomSize;
        }
        
        // ORIGINAL CORRIDOR CONNECTION ALGORITHM (Minimum Spanning Tree)
        private void ConnectRooms()
        {
            if (localRooms.Length < 2)
                return;
            
            // Create minimum spanning tree
            NativeList<RoomData> connectedRooms = new NativeList<RoomData>(Allocator.Temp);
            NativeList<RoomData> unconnectedRooms = new NativeList<RoomData>(Allocator.Temp);
            
            for (int i = 0; i < localRooms.Length; i++)
            {
                unconnectedRooms.Add(localRooms[i]);
            }
            
            int startIndex = random.NextInt(0, unconnectedRooms.Length);
            RoomData startRoom = unconnectedRooms[startIndex];
            connectedRooms.Add(startRoom);
            unconnectedRooms.RemoveAtSwapBack(startIndex);
            
            while (unconnectedRooms.Length > 0)
            {
                float minDistance = float.MaxValue;
                RoomData closestUnconnected = default;
                RoomData closestConnected = default;
                
                for (int i = 0; i < connectedRooms.Length; i++)
                {
                    var connectedRoom = connectedRooms[i];
                    
                    for (int j = 0; j < unconnectedRooms.Length; j++)
                    {
                        var unconnectedRoom = unconnectedRooms[j];
                        float distance = math.distance(connectedRoom.Center, unconnectedRoom.Center);
                        
                        if (distance < minDistance)
                        {
                            minDistance = distance;
                            closestUnconnected = unconnectedRoom;
                            closestConnected = connectedRoom;
                        }
                    }
                }
                
                if (closestUnconnected.id != 0 && closestConnected.id != 0)
                {
                    CreateGeometricCorridor(closestConnected, closestUnconnected);
                    connectedRooms.Add(closestUnconnected);
                    
                    // Remove from unconnected
                    for (int i = 0; i < unconnectedRooms.Length; i++)
                    {
                        if (unconnectedRooms[i].id == closestUnconnected.id)
                        {
                            unconnectedRooms.RemoveAtSwapBack(i);
                            break;
                        }
                    }
                }
                else
                {
                    break;
                }
            }
            
            connectedRooms.Dispose();
            unconnectedRooms.Dispose();
        }
        
        private void CreateGeometricCorridor(RoomData roomA, RoomData roomB)
        {
            bool makeVertical = random.Chance(verticalConnectionChance) && 
                               math.abs(roomA.Center.y - roomB.Center.y) > minRoomSize;
            
            int corridorWidth = minCorridorWidth + random.NextInt(0, maxCorridorWidth - minCorridorWidth + 1);
            
            // Get connection points
            int3 pointA = GetFaceCenter(roomA, roomB.Center);
            int3 pointB = GetFaceCenter(roomB, roomA.Center);
            
            if (!makeVertical)
            {
                pointA.y = roomA.minBounds.y;
                pointB.y = roomB.minBounds.y;
            }
            
            localCorridors.Add(new CorridorData
            {
                id = nextCorridorId++,
                roomAId = roomA.id,
                roomBId = roomB.id,
                isVertical = makeVertical ? 1 : 0,
                start = pointA,
                end = pointB,
                width = corridorWidth,
                height = makeVertical ? verticalShaftSize : corridorHeight,
                isActive = 1
            });
        }
        
        private int3 GetFaceCenter(RoomData room, int3 target)
        {
            int3 bestPoint = room.Center;
            float minDist = math.distance(room.Center, target);
            
            // Check each face directly (no managed arrays in Burst)
            int3 face = new int3(room.minBounds.x, room.Center.y, room.Center.z);
            float dist = math.distance(face, target);
            if (dist < minDist) { minDist = dist; bestPoint = face; }
            
            face = new int3(room.maxBounds.x, room.Center.y, room.Center.z);
            dist = math.distance(face, target);
            if (dist < minDist) { minDist = dist; bestPoint = face; }
            
            face = new int3(room.Center.x, room.minBounds.y, room.Center.z);
            dist = math.distance(face, target);
            if (dist < minDist) { minDist = dist; bestPoint = face; }
            
            face = new int3(room.Center.x, room.maxBounds.y, room.Center.z);
            dist = math.distance(face, target);
            if (dist < minDist) { minDist = dist; bestPoint = face; }
            
            face = new int3(room.Center.x, room.Center.y, room.minBounds.z);
            dist = math.distance(face, target);
            if (dist < minDist) { minDist = dist; bestPoint = face; }
            
            face = new int3(room.Center.x, room.Center.y, room.maxBounds.z);
            dist = math.distance(face, target);
            if (dist < minDist) { minDist = dist; bestPoint = face; }
            
            return bestPoint;
        }
        
        private void CarveRoomsToGrid()
        {
            int3 worldOffset = chunkCoord * chunkSize;
            
            for (int r = 0; r < localRooms.Length; r++)
            {
                var room = localRooms[r];
                CarveCuboid(room.minBounds, room.maxBounds, worldOffset);
            }
        }
        
        private void CarveCorridorsToGrid()
        {
            int3 worldOffset = chunkCoord * chunkSize;
            
            for (int c = 0; c < localCorridors.Length; c++)
            {
                var corridor = localCorridors[c];
                CarveCorridorPath(corridor.start, corridor.end, corridor.width, corridor.height, worldOffset);
            }
        }
        
        private void CarveCuboid(int3 min, int3 max, int3 worldOffset)
        {
            int3 localMin = min - worldOffset;
            int3 localMax = max - worldOffset;
            
            int3 start = math.max(localMin, new int3(0));
            int3 end = math.min(localMax, chunkSize - new int3(1));
            
            for (int x = start.x; x <= end.x; x++)
            {
                for (int y = start.y; y <= end.y; y++)
                {
                    for (int z = start.z; z <= end.z; z++)
                    {
                        int index = x * (chunkSize.y * chunkSize.z) + y * chunkSize.z + z;
                        if (index >= 0 && index < outputGrid.Length)
                            outputGrid[index] = 1;
                    }
                }
            }
        }
        
        // ORIGINAL CORRIDOR PATH GENERATION
        private void CarveCorridorPath(int3 start, int3 end, int width, int height, int3 worldOffset)
        {
            NativeList<int3> path = new NativeList<int3>(16, Allocator.Temp);
            GenerateStraightCorridorPath(start - worldOffset, end - worldOffset, path);
            
            int halfWidth = width / 2;
            
            foreach (var center in path)
            {
                int startY = center.y - height / 2;
                
                for (int dx = -halfWidth; dx <= halfWidth; dx++)
                {
                    for (int dz = -halfWidth; dz <= halfWidth; dz++)
                    {
                        for (int dy = 0; dy < height; dy++)
                        {
                            int3 localPos = new int3(
                                center.x + dx,
                                startY + dy,
                                center.z + dz
                            );
                            
                            if (localPos.x >= 0 && localPos.x < chunkSize.x &&
                                localPos.y >= 0 && localPos.y < chunkSize.y &&
                                localPos.z >= 0 && localPos.z < chunkSize.z)
                            {
                                int index = localPos.x * (chunkSize.y * chunkSize.z) + 
                                           localPos.y * chunkSize.z + 
                                           localPos.z;
                                if (index >= 0 && index < outputGrid.Length)
                                    outputGrid[index] = 1;
                            }
                        }
                    }
                }
            }
            
            path.Dispose();
        }
        
        // ORIGINAL STRAIGHT CORRIDOR PATH ALGORITHM
        private void GenerateStraightCorridorPath(int3 start, int3 end, NativeList<int3> path)
        {
            int3 current = start;
            path.Add(current);
            
            int xDir = math.clamp(end.x - start.x, -1, 1);
            while (current.x != end.x)
            {
                current.x += xDir;
                path.Add(current);
            }
            
            int zDir = math.clamp(end.z - current.z, -1, 1);
            while (current.z != end.z)
            {
                current.z += zDir;
                path.Add(current);
            }
            
            if (current.y != end.y)
            {
                int yDir = math.clamp(end.y - current.y, -1, 1);
                while (current.y != end.y)
                {
                    current.y += yDir;
                    path.Add(current);
                }
            }
        }
        
        private void CopyResults()
        {
            for (int i = 0; i < localRooms.Length; i++)
                newRooms.Add(localRooms[i]);
            
            for (int i = 0; i < localCorridors.Length; i++)
                newCorridors.Add(localCorridors[i]);
        }
        
        private bool IsPointInAnyRoom(int3 worldPos)
        {
            for (int i = 0; i < allRooms.Length; i++)
            {
                if (allRooms[i].isActive == 1 && allRooms[i].ContainsPoint(worldPos))
                    return true;
            }
            
            for (int i = 0; i < localRooms.Length; i++)
            {
                if (localRooms[i].ContainsPoint(worldPos))
                    return true;
            }
            
            return false;
        }
        
        private void ShuffleDirections(ref NativeList<int> list)
        {
            for (int i = 0; i < list.Length; i++)
            {
                int j = random.NextInt(0, list.Length);
                int temp = list[i];
                list[i] = list[j];
                list[j] = temp;
            }
        }
        
        private int3 IndexToDirection(int index)
        {
            switch (index)
            {
                case 0: return new int3(-1, 0, 0);
                case 1: return new int3(1, 0, 0);
                case 2: return new int3(0, -1, 0);
                case 3: return new int3(0, 1, 0);
                case 4: return new int3(0, 0, -1);
                default: return new int3(0, 0, 1);
            }
        }
        
        private float GetNoise(int x, int y, int z, int seed, float3 scale)
        {
            uint hash = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791 ^ seed);
            hash ^= hash << 13;
            hash ^= hash >> 17;
            hash ^= hash << 5;
            return (hash & 0x7FFFFF) / 8388607.0f;
        }
        
        private int GetChunkSeed(int3 coord, int worldSeed)
        {
            return worldSeed ^ (coord.x * 73856093) ^ (coord.y * 19349663) ^ (coord.z * 83492791);
        }
    }
    
    [BurstCompile]
    private struct MergeResultsJob : IJob
    {
        public NativeList<RoomData> allRooms;
        public NativeList<CorridorData> allCorridors;
        public NativeParallelMultiHashMap<int3, int> chunkToRooms;
        public NativeParallelMultiHashMap<int3, int> chunkToCorridors;
        public NativeParallelMultiHashMap<int3, int> roomSpatialGrid;
        public NativeHashSet<int3> processedChunks;
        
        [ReadOnly] public NativeList<RoomData> newRooms;
        [ReadOnly] public NativeList<CorridorData> newCorridors;
        
        public int3 chunkCoord;
        public int3 chunkSize;
        
        public void Execute()
        {
            // Add rooms
            for (int i = 0; i < newRooms.Length; i++)
            {
                var room = newRooms[i];
                allRooms.Add(room);
                AddRoomToMappings(room);
            }
            
            // Add corridors
            for (int i = 0; i < newCorridors.Length; i++)
            {
                var corridor = newCorridors[i];
                allCorridors.Add(corridor);
                
                int3 midPoint = (corridor.start + corridor.end) / 2;
                int3 corridorChunk = new int3(
                    midPoint.x / chunkSize.x,
                    midPoint.y / chunkSize.y,
                    midPoint.z / chunkSize.z
                );
                chunkToCorridors.Add(corridorChunk, corridor.id);
            }
            
            processedChunks.Add(chunkCoord);
        }
        
        private void AddRoomToMappings(RoomData room)
        {
            // Spatial grid
            int3 gridMin = new int3(room.minBounds.x / 32, room.minBounds.y / 32, room.minBounds.z / 32);
            int3 gridMax = new int3(room.maxBounds.x / 32, room.maxBounds.y / 32, room.maxBounds.z / 32);
            
            for (int x = gridMin.x; x <= gridMax.x; x++)
            {
                for (int y = gridMin.y; y <= gridMax.y; y++)
                {
                    for (int z = gridMin.z; z <= gridMax.z; z++)
                    {
                        roomSpatialGrid.Add(new int3(x, y, z), room.id);
                    }
                }
            }
            
            // Chunk mapping
            int3 minChunk = new int3(
                room.minBounds.x / chunkSize.x,
                room.minBounds.y / chunkSize.y,
                room.minBounds.z / chunkSize.z
            );
            int3 maxChunk = new int3(
                room.maxBounds.x / chunkSize.x,
                room.maxBounds.y / chunkSize.y,
                room.maxBounds.z / chunkSize.z
            );
            
            for (int x = minChunk.x; x <= maxChunk.x; x++)
            {
                for (int y = minChunk.y; y <= maxChunk.y; y++)
                {
                    for (int z = minChunk.z; z <= maxChunk.z; z++)
                    {
                        chunkToRooms.Add(new int3(x, y, z), room.id);
                    }
                }
            }
        }
    }
    
    // Fast deterministic random
    public struct FastRandom
    {
        private uint state;
        
        public FastRandom(int seed)
        {
            state = (uint)seed;
        }
        
        public float NextFloat()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0x7FFFFF) / 8388607.0f;
        }
        
        public int NextInt(int min, int max)
        {
            return min + (int)(NextFloat() * (max - min));
        }
        
        public bool Chance(float probability)
        {
            return NextFloat() < probability;
        }
    }
}