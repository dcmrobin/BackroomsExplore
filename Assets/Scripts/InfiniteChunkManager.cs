using UnityEngine;
using System.Collections.Generic;

public class InfiniteChunkManager : MonoBehaviour
{
    [Header("Chunk Settings")]
    public Vector3Int chunkSize = new Vector3Int(80, 40, 80); // Same as original grid size
    [SerializeField] private int renderDistance = 3;
    [SerializeField] private bool useObjectPooling = true;
    
    [Header("References")]
    [SerializeField] private DungeonChunk chunkPrefab;
    [SerializeField] private Transform playerTransform;
    [SerializeField] private Material chunkMaterial;
    
    private ChunkRoomGenerator roomGenerator;
    
    // Chunk storage
    private Dictionary<Vector3Int, DungeonChunk> loadedChunks = new Dictionary<Vector3Int, DungeonChunk>();
    private Dictionary<Vector3Int, bool[,,]> chunkVoxelCache = new Dictionary<Vector3Int, bool[,,]>();
    private Queue<DungeonChunk> chunkPool = new Queue<DungeonChunk>();
    private Transform chunkContainer;
    
    // State
    private Vector3Int currentPlayerChunkCoord = Vector3Int.zero;
    private List<Vector3Int> chunksToGenerate = new List<Vector3Int>();
    
    void Start()
    {
        if (playerTransform == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) playerTransform = player.transform;
            else playerTransform = Camera.main?.transform;
        }
        
        roomGenerator = GetComponent<ChunkRoomGenerator>();
        if (roomGenerator == null)
            roomGenerator = gameObject.AddComponent<ChunkRoomGenerator>();
        
        chunkContainer = new GameObject("Chunks").transform;
        chunkContainer.SetParent(transform);
        
        InitializeObjectPool();
        GenerateInitialChunks();
    }
    
    void Update()
    {
        if (playerTransform == null) return;
        
        UpdatePlayerChunk();
        ManageChunks();
        GenerateQueuedChunks();
    }
    
    private void UpdatePlayerChunk()
    {
        Vector3 playerPos = playerTransform.position;
        Vector3Int newChunkCoord = WorldToChunkCoord(playerPos);
        
        if (newChunkCoord != currentPlayerChunkCoord)
        {
            currentPlayerChunkCoord = newChunkCoord;
        }
    }
    
    private Vector3Int WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(worldPos.x / chunkSize.x),
            Mathf.FloorToInt(worldPos.y / chunkSize.y),
            Mathf.FloorToInt(worldPos.z / chunkSize.z)
        );
    }
    
    private Vector3 ChunkCoordToWorld(Vector3Int chunkCoord)
    {
        return new Vector3(
            chunkCoord.x * chunkSize.x,
            chunkCoord.y * chunkSize.y,
            chunkCoord.z * chunkSize.z
        );
    }
    
    private void ManageChunks()
    {
        // Unload distant chunks
        List<Vector3Int> chunksToUnload = new List<Vector3Int>();
        foreach (var kvp in loadedChunks)
        {
            int distance = GetChunkDistance(kvp.Key, currentPlayerChunkCoord);
            if (distance > renderDistance)
            {
                chunksToUnload.Add(kvp.Key);
            }
        }
        
        foreach (var coord in chunksToUnload)
        {
            UnloadChunk(coord);
        }
        
        // Queue new chunks
        for (int x = -renderDistance; x <= renderDistance; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -renderDistance; z <= renderDistance; z++)
                {
                    Vector3Int chunkCoord = currentPlayerChunkCoord + new Vector3Int(x, y, z);
                    
                    if (!loadedChunks.ContainsKey(chunkCoord) && 
                        !chunksToGenerate.Contains(chunkCoord))
                    {
                        chunksToGenerate.Add(chunkCoord);
                    }
                }
            }
        }
    }
    
    private void GenerateQueuedChunks()
    {
        if (chunksToGenerate.Count == 0) return;
        
        // Generate one chunk per frame (room generation is heavy)
        Vector3Int chunkCoord = chunksToGenerate[0];
        chunksToGenerate.RemoveAt(0);
        
        GenerateChunkImmediate(chunkCoord);
    }
    
    private void GenerateChunkImmediate(Vector3Int chunkCoord)
    {
        if (loadedChunks.ContainsKey(chunkCoord)) return;
        
        DungeonChunk chunk = GetChunkFromPool();
        Vector3 worldPos = ChunkCoordToWorld(chunkCoord);
        chunk.transform.position = worldPos;
        chunk.transform.SetParent(chunkContainer);
        chunk.name = $"Chunk_{chunkCoord.x}_{chunkCoord.y}_{chunkCoord.z}";
        
        // Set material
        MeshRenderer renderer = chunk.GetComponent<MeshRenderer>();
        if (renderer != null && chunkMaterial != null)
        {
            renderer.material = new Material(chunkMaterial);
        }
        
        // Generate or retrieve chunk data
        if (!chunkVoxelCache.TryGetValue(chunkCoord, out bool[,,] voxelGrid))
        {
            voxelGrid = new bool[chunkSize.x, chunkSize.y, chunkSize.z];
            roomGenerator.GenerateForChunk(chunkCoord, chunkSize, ref voxelGrid);
            chunkVoxelCache[chunkCoord] = voxelGrid;
        }
        
        // Generate mesh
        chunk.Initialize(chunkSize);
        chunk.GenerateMesh(voxelGrid);
        
        loadedChunks[chunkCoord] = chunk;
    }
    
    private int GetChunkDistance(Vector3Int a, Vector3Int b)
    {
        return Mathf.Max(
            Mathf.Abs(a.x - b.x),
            Mathf.Abs(a.y - b.y),
            Mathf.Abs(a.z - b.z)
        );
    }
    
    private void UnloadChunk(Vector3Int chunkCoord)
    {
        if (loadedChunks.TryGetValue(chunkCoord, out DungeonChunk chunk))
        {
            ReturnChunkToPool(chunk);
            loadedChunks.Remove(chunkCoord);
            
            // Clear generator data for this chunk
            roomGenerator.ClearChunkData(chunkCoord);
            chunkVoxelCache.Remove(chunkCoord);
        }
    }
    
    private void InitializeObjectPool()
    {
        if (chunkPrefab == null)
        {
            Debug.LogError("Chunk Prefab is not assigned!");
            return;
        }
        
        for (int i = 0; i < 10; i++)
        {
            DungeonChunk chunk = Instantiate(chunkPrefab);
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk);
        }
    }
    
    private void GenerateInitialChunks()
    {
        // Generate initial area
        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                Vector3Int coord = new Vector3Int(x, 0, z);
                if (!chunksToGenerate.Contains(coord))
                    chunksToGenerate.Add(coord);
            }
        }
    }
    
    private DungeonChunk GetChunkFromPool()
    {
        if (useObjectPooling && chunkPool.Count > 0)
        {
            DungeonChunk chunk = chunkPool.Dequeue();
            chunk.gameObject.SetActive(true);
            return chunk;
        }
        else
        {
            DungeonChunk chunk = Instantiate(chunkPrefab);
            return chunk;
        }
    }
    
    private void ReturnChunkToPool(DungeonChunk chunk)
    {
        if (useObjectPooling)
        {
            chunk.Clear();
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk);
        }
        else
        {
            Destroy(chunk.gameObject);
        }
    }
}