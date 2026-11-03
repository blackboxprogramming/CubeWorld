using UnityEngine;
using UnityEngine.Networking;
using System.Collections.Generic;
using CubeWorld.Configuration;

public class GameManagerUnity : MonoBehaviour
{
    public enum GameManagerUnityState
    {
        MAIN_MENU,
        GAME,
        GENERATING,
        PAUSE
    }

    public CubeWorld.World.CubeWorld world;

    public Material material;
    public Material materialTransparent;
    public Material materialTranslucid;
    public Material materialDamaged;
    public Material materialItems;
	
	public Material materialLiquidAnimated;

    public CubeWorld.Configuration.ConfigExtraMaterials extraMaterials;

    public SurroundingsUnity surroundingsUnity;
    public WorldManagerUnity worldManagerUnity;

    [HideInInspector]
    public PlayerUnity playerUnity;

    private GameManagerUnityState state;
    private GameManagerUnityState newState;
    private MainMenu mainMenu;

	public SectorManagerUnity sectorManagerUnity;
	
	public CWObjectsManagerUnity objectsManagerUnity;
    public CWFxManagerUnity fxManagerUnity;

    public GameManagerUnityState State
    {
        get { return newState; }
        set { this.newState = value; }
    }

    public void Start()
    {
        Application.logMessageReceived += HandleLog;

        MeshUtils.InitStaticValues();
        CubeWorldPlayerPreferences.LoadPreferences();
        PreferencesUpdated();

        State = state = GameManagerUnityState.MAIN_MENU;
        mainMenu = new MainMenu(this);

		sectorManagerUnity = new SectorManagerUnity(this);
		objectsManagerUnity = new CWObjectsManagerUnity(this);
        fxManagerUnity = new CWFxManagerUnity(this);

        worldManagerUnity = new WorldManagerUnity(this);
	}

    public void DestroyWorld()
    {
#if !UNITY_WEBPLAYER
        StopDiscoveryRegistration();
#endif
		objectsManagerUnity.Clear();
		
		sectorManagerUnity.Clear();

        if (world != null)
        {
            world.Clear();
            world = null;
        }

        surroundingsUnity.Clear();
		
		playerUnity = null;

        System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced);
    }

    static private string GetConfigText(string resourceName)
    {
        string configText = ((TextAsset)Resources.Load(resourceName)).text;

        #if !UNITY_WEBPLAYER

        if (Application.isEditor == false && Application.platform != RuntimePlatform.WebGLPlayer)
        {
            try
            {
                string exePath = System.IO.Directory.GetParent(Application.dataPath).FullName;
                string fileConfigPath = System.IO.Path.Combine(exePath, resourceName + ".xml");

                if (System.IO.File.Exists(fileConfigPath))
                    configText = System.IO.File.ReadAllText(fileConfigPath);
            }
            catch (System.Exception ex)
            {
                Debug.Log(ex.ToString());
            }
        }

        #endif

        return configText;
    }

    static public AvailableConfigurations LoadConfiguration()
    {
        AvailableConfigurations availableConfigurations = 
            new ConfigParserXML().Parse(
                GetConfigText("config_misc"),
                GetConfigText("config_tiles"),
                GetConfigText("config_avatars"),
                GetConfigText("config_items"),
                GetConfigText("config_generators"));

        return availableConfigurations;
    }

    public void LoadCustomTextures()
    {
        #if !UNITY_WEBPLAYER

            if (Application.isEditor == false && Application.platform != RuntimePlatform.WebGLPlayer)
            {
                try
                {
                    string exePath = System.IO.Directory.GetParent(Application.dataPath).FullName;
                    string fileTexturePath = System.IO.Path.Combine(exePath, "TexturaPrincipal.png");

                    if (System.IO.File.Exists(fileTexturePath))
                    {
                        Texture2D texture = (Texture2D) material.GetTexture("_MainTex");

                        texture.LoadImage(System.IO.File.ReadAllBytes(fileTexturePath));

                        material.mainTexture = texture;
                        materialTransparent.mainTexture = texture;
                        materialTranslucid.mainTexture = texture;
                        materialDamaged.mainTexture = texture;
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.Log(ex.ToString());
                }
            }

        #endif
    }

    private static string DiscoveryUrl
    {
        get
        {
            string configured = System.Environment.GetEnvironmentVariable(DiscoveryProtocol.EnvironmentKey);
            return string.IsNullOrEmpty(configured) ? DiscoveryProtocol.DefaultUrl : configured;
        }
    }

    public static System.Uri DiscoveryEndpoint(string route)
    {
        return DiscoveryProtocol.Endpoint(DiscoveryUrl, route);
    }

#if !UNITY_WEBPLAYER
    private UnityWebRequest registration;
    private float nextRegistration;

    private void UpdateDiscoveryRegistration()
    {
        if (registration != null)
        {
            if (!registration.isDone)
                return;

            if (registration.isNetworkError || registration.isHttpError
                || registration.downloadHandler.text != "OK")
                Debug.LogWarning("CubeWorld could not publish the hosted game to discovery.");
            registration.Dispose();
            registration = null;
        }

        if (Time.realtimeSinceStartup < nextRegistration)
            return;

        nextRegistration = Time.realtimeSinceStartup + DiscoveryProtocol.HeartbeatSeconds;
        try
        {
            System.Uri url = DiscoveryProtocol.Register(DiscoveryUrl,
                CubeWorld.Gameplay.MultiplayerServerGameplay.SERVER_PORT, "CubeWorld", "Hosted game");
            registration = UnityWebRequest.Get(url.AbsoluteUri);
            registration.timeout = 5;
            registration.SendWebRequest();
        }
        catch (System.ArgumentException)
        {
            if (registration != null)
            {
                registration.Dispose();
                registration = null;
            }
            Debug.LogWarning("CubeWorld discovery requires a valid HTTP(S) base URL.");
        }
    }

    private void StopDiscoveryRegistration()
    {
        if (registration != null)
        {
            registration.Dispose();
            registration = null;
        }
        nextRegistration = 0;
    }
#endif

    private void OnDestroy()
    {
        Application.logMessageReceived -= HandleLog;
        if (mainMenu != null)
            mainMenu.CloseDiscovery();
#if !UNITY_WEBPLAYER
        StopDiscoveryRegistration();
#endif
    }

    public void StartGame()
    {
        GetComponent<Camera>().enabled = false;

        LockCursor();

        State = GameManagerUnityState.GAME;
    }

    public void Pause()
    {
        ReleaseCursor();

        State = GameManagerUnityState.PAUSE;
    }

    public void Unpause()
    {
        LockCursor();

        State = GameManagerUnityState.GAME;
    }

    public void ReturnToMainMenu()
    {
        DestroyWorld();

        GetComponent<Camera>().enabled = true;

        State = GameManagerUnityState.MAIN_MENU;
    }

    public void Update()
    {
#if !UNITY_WEBPLAYER
        if ((state == GameManagerUnityState.GAME || state == GameManagerUnityState.PAUSE)
            && world != null && world.gameplay is CubeWorld.Gameplay.MultiplayerServerGameplay)
            UpdateDiscoveryRegistration();
#endif

        switch (state)
        {
            case GameManagerUnityState.GENERATING:
            {
                if (worldManagerUnity.worldGeneratorProcess != null && worldManagerUnity.worldGeneratorProcess.IsFinished() == false)
                {
                    worldManagerUnity.worldGeneratorProcess.Generate();
                }
                else
                {
                    worldManagerUnity.worldGeneratorProcess = null;
				
                    PreferencesUpdated();

                    StartGame();
                }

                break;
            }

            case GameManagerUnityState.PAUSE:
            case GameManagerUnityState.GAME:
            {
                if (world != null)
                {
                    surroundingsUnity.UpdateSkyColor();
                    playerUnity.UpdateControlled();
                    world.Update(Time.deltaTime);
                    UpdateAnimatedTexture();
                }
                break;
            }
        }

        if (newState != state)
            state = newState;
    }
	
	private float textureAnimationTimer;
	private int animFrames = 5;
	private int animFrame;
	private int textureAnimationFPS = 2;
	
	private void UpdateAnimatedTexture()
	{
		textureAnimationTimer += Time.deltaTime;
		
		if (textureAnimationTimer > 1.0f / textureAnimationFPS)
		{
			textureAnimationTimer = 0.0f;
			
			animFrame++;
			
			float uvdelta = 1.0f / GraphicsUnity.TILE_PER_MATERIAL_ROW;
			
			materialLiquidAnimated.mainTextureOffset = new Vector2(-(animFrame % animFrames) * uvdelta, 0.0f);
		}
	}

    public void OnGUI()
    {
        switch (state)
        {
            case GameManagerUnityState.GENERATING:
                if (worldManagerUnity.worldGeneratorProcess != null)
                    mainMenu.DrawGeneratingProgress(worldManagerUnity.worldGeneratorProcess.ToString(), worldManagerUnity.worldGeneratorProcess.GetProgress());
                break;

            case GameManagerUnityState.MAIN_MENU:
                mainMenu.Draw();
                break;

            case GameManagerUnityState.PAUSE:
                mainMenu.DrawPause();
                break;
        }

        if (internalErrorLog != null)
        {
            GUI.TextArea(new Rect(0, 0, Screen.width, Screen.height / 4), internalErrorLog);
            if (GUI.Button(new Rect(0, Screen.height / 4, 100, 20), "Clear Log"))
            {
                internalErrorLog = null;
            }
        }
    }

    public void ReleaseCursor()
    {
        Screen.lockCursor = false;
        Cursor.visible = true;
    }

    public void LockCursor()
    {
        Screen.lockCursor = true;
        Cursor.visible = false;
    }

    public void PreferencesUpdated()
    {
        if (playerUnity)
        {
            playerUnity.mainCamera.farClipPlane = CubeWorldPlayerPreferences.farClipPlanes[CubeWorldPlayerPreferences.viewDistance];
        }
    }

    private string internalErrorLog = null;

    private void HandleLog(string log, string stackTrace, LogType type) 
    {
        if (internalErrorLog == null)
            internalErrorLog = "";
        internalErrorLog += log + "\n";
    }
}

