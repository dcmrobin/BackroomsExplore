using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
public class DungeonMaterialSetup : MonoBehaviour
{
    [Header("Textures")]
    public Texture2D wallTexture;
    public Texture2D floorTexture;
    public Texture2D ceilingTexture;
    
    [Header("Shader Properties")]
    public float ambientLight = 0.15f;
    public float maxLight = 1.0f;
    public Vector2 textureScale = Vector2.one;
    
    private Material instanceMaterial;
    
    void Start()
    {
        SetupMaterial();
    }
    
    public void SetupMaterial()
    {
        MeshRenderer renderer = GetComponent<MeshRenderer>();
        
        if (renderer.material == null) return;
        
        // Create instance material
        instanceMaterial = new Material(renderer.material);
        renderer.material = instanceMaterial;
        
        // Apply textures if provided
        if (wallTexture != null)
            instanceMaterial.SetTexture("_WallTex", wallTexture);
        
        if (floorTexture != null)
            instanceMaterial.SetTexture("_FloorTex", floorTexture);
        
        if (ceilingTexture != null)
            instanceMaterial.SetTexture("_CeilingTex", ceilingTexture);
        
        // Apply lighting properties
        instanceMaterial.SetFloat("_Ambient", ambientLight);
        instanceMaterial.SetFloat("_MaxLight", maxLight);
        instanceMaterial.SetVector("_TextureScale", textureScale);
    }
    
    public void SetLightLevel(float lightLevel)
    {
        // This could be used to update lighting dynamically
        // You'd need to modify vertex colors instead
    }
}