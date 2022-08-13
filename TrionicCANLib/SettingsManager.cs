using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using NLog;

namespace FlasherSettings
{
    public class SettingProperty
    {
        public SettingProperty(string PropertyName, Object[] ListValues, string RegistryName, string DisplayName, bool Enabled = true)
        {
            if (DisplayName == "")
            {
                DisplayName = PropertyName;
            }

            _propname = PropertyName;
            _listval = ListValues;
            _regname = RegistryName;
            _dispname = DisplayName;
            _enabled = Enabled;
        }

        public readonly bool _enabled;
        public readonly string _propname;
        public readonly Object[] _listval;
        public readonly string _regname;
        public readonly string _dispname;
    }

    public class SettingProperties
    {
        private SettingProperty[] properties = new SettingProperty[] { };

        // Registry subfolder, if any
        public virtual string RegistryKey
        {
            get { return ""; }
        }

        public virtual ref SettingProperty[] Properties
        {
            get { return ref properties; }
        }
    }

    // This is merely a copied instance
    public class SettingCopy
    {
        public SettingCopy(int index, Object value, Object[] listvalues, string displayname, string propname, bool enabled)
        {
            Index = index;
            Value = value;
            ListValues = listvalues;
            DisplayName = displayname;
            Enabled = enabled;
            PropertyName = propname;
        }

        public Type Type
        {
            get { return Value.GetType(); }
        }

        public bool Enabled;
        public Object Value;
        public readonly int Index;
        public readonly string PropertyName;
        public readonly string DisplayName;
        public readonly Object[] ListValues;
    }

    public class SettingsManager
    {
        private Logger m_logger = LogManager.GetCurrentClassLogger();
        private SettingProperty[] Settings;
        private List<SettingCopy> LocalSettings = null;
        private Object m_instance;

        public SettingsManager(Object Instance, SettingProperties props)
        {
            if (props == null)
            {
                props = new SettingProperties();
            }

            m_instance = Instance;
            Settings = props.Properties;
            RegistryKey = props.RegistryKey;

            // Generate a local copy
            GenerateLocalCopy();
        }

        // Source settings -> Local settings
        public void GenerateLocalCopy()
        {
            LocalSettings = new List<SettingCopy>();

            try
            {
                for (int i = 0; i < Settings.Count(); i++)
                {
                    LocalSettings.Add(new SettingCopy(i, Get(i), ListValues(i), DisplayName(i), PropertyName(i), Enabled(i)));
                }
            }
            catch (Exception ex)
            {
                LocalSettings = new List<SettingCopy>();
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
            }
        }

        // Local settings -> Source settings
        public void StoreLocalCopy()
        {
            if (LocalSettings != null)
            {
                foreach (SettingCopy cpy in LocalSettings)
                {
                    SetInternal(cpy.Index, cpy.Value);
                }
            }
        }

        // Local settings
        public ref List<SettingCopy> GetLocalCopy()
        {
            if (LocalSettings == null)
            {
                GenerateLocalCopy();
            }

            return ref LocalSettings;
        }

        // Local setting
        public void Enable(string PropertyName, bool state)
        {
            try
            {
                foreach (SettingCopy set in LocalSettings)
                {
                    if (set.PropertyName == PropertyName)
                    {
                        set.Enabled = state;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
            }
        }

        // Local setting
        public Object Get(string PropertyName, Type type)
        {
            try
            {
                foreach (SettingCopy set in LocalSettings)
                {
                    if (set.PropertyName == PropertyName && type == set.Value.GetType())
                    {
                        return set.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
            }

            return null;
        }

        // Local setting
        public bool Set(string PropertyName, Object Value)
        {
            try
            {
                foreach (SettingCopy set in LocalSettings)
                {
                    if (set.PropertyName == PropertyName && Value.GetType() == set.Value.GetType())
                    {
                        set.Value = Value;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
            }

            return false;
        }

        // Source setting
        public bool Enabled(int idx)
        {
            if (Settings == null || idx < 0 || idx >= Settings.Count())
            {
                return false; ;
            }

            return Settings[idx]._enabled;
        }

        // Source setting
        public readonly string RegistryKey;

        // Source setting
        public string RegistryName(int idx)
        {
            if (Settings == null || idx < 0 || idx >= Settings.Count())
            {
                return "";
            }

            return Settings[idx]._regname;
        }

        // Source setting
        public int Count
        {
            get
            {
                return (Settings != null) ? Settings.Count() : 0;
            }
        }

        // Source setting
        public Object[] ListValues(int idx)
        {
            if (Settings == null || idx < 0 || idx >= Settings.Count())
            {
                return null;
            }

            return Settings[idx]._listval;
        }

        // Source setting
        public string DisplayName(int idx)
        {
            if (Settings == null || idx < 0 || idx >= Settings.Count())
            {
                return "No name";
            }

            return Settings[idx]._dispname;
        }

        // Source setting
        public string PropertyName(int idx)
        {
            if (Settings == null || idx < 0 || idx >= Settings.Count())
            {
                return "";
            }

            return Settings[idx]._propname;
        }

        // Source setting
        public Type Type(int idx)
        {
            Type typ = null;

            if (idx < 0 || idx >= Settings.Count())
            {
                return typ;
            }

            try
            {
                PropertyInfo prop = m_instance.GetType().GetProperty(Settings[idx]._propname);
                typ = prop.PropertyType;
            }
            catch (Exception ex)
            {
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
            }

            return typ;
        }

        // Source setting
        public Object Get(int idx)
        {
            Object value = 0;

            if (idx < 0 || idx >= Settings.Count())
            {
                return null;
            }

            try
            {
                PropertyInfo prop = m_instance.GetType().GetProperty(Settings[idx]._propname);

                if (prop.PropertyType == typeof(bool))
                {
                    Func<bool> Getter = (Func<bool>)
                        Delegate.CreateDelegate(typeof(Func<bool>), m_instance, prop.GetGetMethod());
                    value = Getter();
                }
                else if (prop.PropertyType == typeof(int))
                {
                    Func<int> Getter = (Func<int>)
                        Delegate.CreateDelegate(typeof(Func<int>), m_instance, prop.GetGetMethod());
                    value = Getter();

                    // Automatically fix up index if it's out of bounds
                    if (Settings[idx]._listval != null)
                    {
                        if ((int)value < 0 || Settings[idx]._listval.Count() == 0)
                        {
                            value = 0;
                            Set(idx, (int)value);
                            m_logger.Debug("Fixed up list bounds");
                        }
                        else if ((int)value >= Settings[idx]._listval.Count())
                        {
                            value = (int)Settings[idx]._listval.Count() - 1;
                            Set(idx, (int)value);
                            m_logger.Debug("Fixed up list bounds");
                        }
                    }
                }
                else
                {
                    throw new Exception("There's no support for " + prop.PropertyType);
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
                return null;
            }

            return value;
        }

        // Source setting
        public bool Set(int idx, int value)
        {
            if (idx < 0 || idx >= Settings.Count())
            {
                return false;
            }

            try
            {
                PropertyInfo prop = m_instance.GetType().GetProperty(Settings[idx]._propname);

                if (prop.PropertyType == typeof(int))
                {
                    Action<int> Setter =
                        (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), m_instance, prop.GetSetMethod());
                    
                    // Automatically fix up index if it's out of bounds
                    if (Settings[idx]._listval != null)
                    {
                        if ((int)value < 0 || Settings[idx]._listval.Count() == 0)
                        {
                            value = 0;
                            m_logger.Debug("Fixed up list bounds");
                        }
                        else if ((int)value >= Settings[idx]._listval.Count())
                        {
                            value = (int)Settings[idx]._listval.Count() - 1;
                            m_logger.Debug("Fixed up list bounds");
                        }
                    }

                    Setter(value);
                }
                else
                {
                    throw new Exception("Tried setting " + prop.PropertyType + " as type int");
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
                return false;
            }

            return true;
        }

        // Source setting
        public bool Set(int idx, bool value)
        {
            if (idx < 0 || idx >= Settings.Count())
            {
                return false;
            }

            try
            {
                PropertyInfo prop = m_instance.GetType().GetProperty(Settings[idx]._propname);

                if (prop.PropertyType == typeof(bool))
                {
                    Action<bool> Setter =
                        (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), m_instance, prop.GetSetMethod());
                    Setter(value);
                }
                else
                {
                    throw new Exception("Tried setting " + prop.PropertyType + " as type bool");
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
                return false;
            }

            return true;
        }

        // Source setting
        private void SetInternal(int idx, Object value)
        {
            try
            {
                PropertyInfo prop = m_instance.GetType().GetProperty(Settings[idx]._propname);

                if (prop.PropertyType == typeof(int))
                {
                    Action<int> Setter =
                        (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), m_instance, prop.GetSetMethod());

                    // Automatically fix up index if it's out of bounds
                    if (Settings[idx]._listval != null)
                    {
                        if ((int)value < 0 || Settings[idx]._listval.Count() == 0)
                        {
                            value = 0;
                            m_logger.Debug("Fixed up list bounds");
                        }
                        else if ((int)value >= Settings[idx]._listval.Count())
                        {
                            value = (int)Settings[idx]._listval.Count() - 1;
                            m_logger.Debug("Fixed up list bounds");
                        }
                    }

                    Setter((int)value);
                }
                else if (prop.PropertyType == typeof(bool))
                {
                    Action<bool> Setter =
                        (Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>), m_instance, prop.GetSetMethod());
                    Setter((bool)value);
                }
                else
                {
                    throw new Exception("Tried setting " + prop.PropertyType + " as type int");
                }
            }
            catch (Exception ex)
            {
                m_logger.Debug("Settings API exception: " + ex.ToString());
                ErrorCounter++;
            }
        }

        // To be removed after verification
        public int ErrorCounter = 0;
    }
}