using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

// Shared structs for all dungeon generation jobs
public struct RoomData
{
    public int3 minBounds;
    public int3 maxBounds;
    public int id;
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
    
    public NativeList<int3> GetPath()
    {
        var path = new NativeList<int3>(Allocator.Temp);
        int3 current = start;
        
        // Simple L-shaped path
        while (current.x != end.x)
        {
            path.Add(current);
            current.x += math.clamp(end.x - current.x, -1, 1);
        }
        
        while (current.z != end.z)
        {
            path.Add(current);
            current.z += math.clamp(end.z - current.z, -1, 1);
        }
        
        while (current.y != end.y)
        {
            path.Add(current);
            current.y += math.clamp(end.y - current.y, -1, 1);
        }
        
        path.Add(end);
        return path;
    }
}

public struct VoxelData
{
    public byte value;  // 0 = air, 1 = solid, 2 = light source, etc.
    public byte lightLevel;
    public byte materialID;
}

// Fast deterministic random for jobs
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