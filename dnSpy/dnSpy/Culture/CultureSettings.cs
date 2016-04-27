﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings;
using dnSpy.Shared.MVVM;

namespace dnSpy.Culture {
	interface ICultureSettings : INotifyPropertyChanged {
		string UIName { get; }
	}

	class CultureSettings : ViewModelBase, ICultureSettings {
		protected virtual void OnModified() {
		}

		public string UIName {
			get { return uiName; }
			set {
				if (uiName != value) {
					uiName = value;
					OnPropertyChanged("UIName");
					OnModified();
				}
			}
		}
		string uiName = string.Empty;
	}

	[Export, Export(typeof(ICultureSettings)), PartCreationPolicy(CreationPolicy.Shared)]
	sealed class CultureSettingsImpl : CultureSettings {
		static readonly Guid SETTINGS_GUID = new Guid("4D05C47D-3F6A-429E-9CB3-232E10D45468");

		readonly ISettingsManager settingsManager;

		[ImportingConstructor]
		CultureSettingsImpl(ISettingsManager settingsManager) {
			this.settingsManager = settingsManager;

			this.disableSave = true;
			var sect = settingsManager.GetOrCreateSection(SETTINGS_GUID);
			this.UIName = sect.Attribute<string>("UIName") ?? this.UIName;
			this.disableSave = false;
		}
		readonly bool disableSave;

		protected override void OnModified() {
			if (disableSave)
				return;
			var sect = settingsManager.RecreateSection(SETTINGS_GUID);
			sect.Attribute("UIName", UIName);
		}
	}
}