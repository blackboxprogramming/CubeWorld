using System;
using CubeWorld.Configuration;
using CubeWorld.Utils;
using CubeWorld.Tiles;
using SourceCode.CubeWorld.Utils;
namespace CubeWorld.World.Lights
{
	public class DayCycleManager
	{
		public CubeWorld world;
		
        public Color skyColorDay = new Color(0.5f, 0.5f, 0.5f);
        public Color skyColorNight = new Color(0.0f, 0.0f, 0.0f);
        public float dayDuration;

        public Color skyColor;
        public byte ambientLightLuminance;

        private float dayTime = 0.0f;
        private DayTimeLuminanceInfo[] dayTimeLuminances;

		public DayCycleManager (CubeWorld world)
		{
			this.world = world;

            this.skyColor = skyColorDay;
            this.ambientLightLuminance = Tile.MAX_LUMINANCE;
		}
		
		public void Create(ConfigDayInfo configDayInfo)
		{
            if (configDayInfo != null)
            {
                this.dayTimeLuminances = configDayInfo.dayTimeLuminances;
                this.dayDuration = configDayInfo.dayDuration;
            }
		}
		
		public void Update(float deltaTime)
		{
            if (dayDuration <= 0 || dayTimeLuminances == null || dayTimeLuminances.Length == 0)
                return;

            dayTime += deltaTime;
            if (dayTime > dayDuration)
                dayTime %= dayDuration;

            float normalizedDayTime = dayTime / dayDuration;
            float luminance = dayTimeLuminances[0].luminancePercent;
            for (int i = 1; i < dayTimeLuminances.Length; i++)
            {
                DayTimeLuminanceInfo source = dayTimeLuminances[i - 1];
                DayTimeLuminanceInfo target = dayTimeLuminances[i];
                if (normalizedDayTime <= source.toTimePercent)
                    break;

                if (normalizedDayTime <= target.toTimePercent)
                {
                    float fraction = (normalizedDayTime - source.toTimePercent)
                        / (target.toTimePercent - source.toTimePercent);
                    luminance = source.luminancePercent
                        + (target.luminancePercent - source.luminancePercent) * fraction;
                    break;
                }

                luminance = target.luminancePercent;
            }

            byte newLuminance = (byte)(Tile.MAX_LUMINANCE * luminance / 100);

            if (newLuminance != ambientLightLuminance)
            {
                ambientLightLuminance = newLuminance;

                float percentLuminance = (float) ambientLightLuminance / (float) Tile.MAX_LUMINANCE;

                skyColor = new Color(
                    skyColorNight.r + (skyColorDay.r - skyColorNight.r) * percentLuminance,
                    skyColorNight.g + (skyColorDay.g - skyColorNight.g) * percentLuminance,
                    skyColorNight.b + (skyColorDay.b - skyColorNight.b) * percentLuminance);

                world.sectorManager.UpdateAllTilesLight();
            }
		}
		
		public void Clear()
		{
            dayTimeLuminances = null;
		}

        public void Save(System.IO.BinaryWriter bw)
        {
            bw.Write(dayDuration);
            SerializationUtils.Write(bw, skyColor);
            bw.Write(ambientLightLuminance);
            bw.Write(dayTime);

            bw.Write(dayTimeLuminances.Length);
            foreach (DayTimeLuminanceInfo dayInfo in dayTimeLuminances)
            {
                bw.Write(dayInfo.luminancePercent);
                bw.Write(dayInfo.toTimePercent);
            }
        }

        public void Load(System.IO.BinaryReader br)
        {
            dayDuration = br.ReadSingle();
            skyColor = SerializationUtils.ReadColor(br);
            ambientLightLuminance = br.ReadByte();
            dayTime = br.ReadSingle();

            int n = br.ReadInt32();
            dayTimeLuminances = new DayTimeLuminanceInfo[n];
            for (int i = 0; i < n; i++)
            {
                int lp = br.ReadInt32();
                float ttp = br.ReadSingle();

                dayTimeLuminances[i] = new DayTimeLuminanceInfo(ttp, lp);
            }
        }
    }
}

