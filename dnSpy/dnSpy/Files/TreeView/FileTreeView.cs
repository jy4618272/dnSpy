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
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Files;
using dnSpy.Contracts.Files.TreeView;
using dnSpy.Contracts.Files.TreeView.Resources;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Languages;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Search;
using dnSpy.Contracts.Themes;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Files.TreeView {
	[Export, Export(typeof(IFileTreeView))]
	sealed class FileTreeView : IFileTreeView, ITreeViewListener {
		readonly FileTreeNodeDataContext context;
		readonly Lazy<IDnSpyFileNodeProvider, IDnSpyFileNodeProviderMetadata>[] dnSpyFileNodeProviders;
		readonly Lazy<IFileTreeNodeDataFinder, IFileTreeNodeDataFinderMetadata>[] nodeFinders;

		public IFileTreeNodeGroups FileTreeNodeGroups => fileTreeNodeGroups;
		readonly FileTreeNodeGroups fileTreeNodeGroups;

		public IFileManager FileManager { get; }
		public ITreeView TreeView { get; }
		IEnumerable<IDnSpyFileNode> TopNodes => TreeView.Root.Children.Select(a => (IDnSpyFileNode)a.Data);
		public IDotNetImageManager DotNetImageManager { get; }
		public IWpfCommands WpfCommands { get; }
		public event EventHandler<NotifyFileTreeViewCollectionChangedEventArgs> CollectionChanged;

		void CallCollectionChanged(NotifyFileTreeViewCollectionChangedEventArgs eventArgs) =>
			CollectionChanged?.Invoke(this, eventArgs);
		public event EventHandler<FileTreeNodeActivatedEventArgs> NodeActivated;

		public bool RaiseNodeActivated(IFileTreeNodeData node) {
			if (node == null)
				throw new ArgumentNullException(nameof(node));
			if (NodeActivated == null)
				return false;
			var e = new FileTreeNodeActivatedEventArgs(node);
			NodeActivated(this, e);
			return e.Handled;
		}

		public event EventHandler<TVSelectionChangedEventArgs> SelectionChanged;
		bool disable_SelectionChanged = false;

		void TreeView_SelectionChanged(object sender, TVSelectionChangedEventArgs e) {
			if (disable_SelectionChanged)
				return;
			SelectionChanged?.Invoke(this, e);
		}

		sealed class GuidObjectsProvider : IGuidObjectsProvider {
			readonly ITreeView treeView;

			public GuidObjectsProvider(ITreeView treeView) {
				this.treeView = treeView;
			}

			public IEnumerable<GuidObject> GetGuidObjects(GuidObjectsProviderArgs args) {
				yield return new GuidObject(MenuConstants.GUIDOBJ_TREEVIEW_NODES_ARRAY_GUID, treeView.TopLevelSelection);
			}
		}

		[ImportingConstructor]
		FileTreeView(IThemeManager themeManager, ITreeViewManager treeViewManager, ILanguageManager languageManager, IFileManager fileManager, IFileTreeViewSettings fileTreeViewSettings, IMenuManager menuManager, IDotNetImageManager dotNetImageManager, IWpfCommandManager wpfCommandManager, IResourceNodeFactory resourceNodeFactory, IAppSettings appSettings, [ImportMany] IEnumerable<Lazy<IDnSpyFileNodeProvider, IDnSpyFileNodeProviderMetadata>> dnSpyFileNodeProviders, [ImportMany] IEnumerable<Lazy<IFileTreeNodeDataFinder, IFileTreeNodeDataFinderMetadata>> mefFinders)
			: this(true, null, themeManager, treeViewManager, languageManager, fileManager, fileTreeViewSettings, menuManager, dotNetImageManager, wpfCommandManager, resourceNodeFactory, appSettings, dnSpyFileNodeProviders, mefFinders) {
		}

		readonly ILanguageManager languageManager;
		readonly IThemeManager themeManager;
		readonly IFileTreeViewSettings fileTreeViewSettings;
		readonly IAppSettings appSettings;

		public FileTreeView(bool isGlobal, IFileTreeNodeFilter filter, IThemeManager themeManager, ITreeViewManager treeViewManager, ILanguageManager languageManager, IFileManager fileManager, IFileTreeViewSettings fileTreeViewSettings, IMenuManager menuManager, IDotNetImageManager dotNetImageManager, IWpfCommandManager wpfCommandManager, IResourceNodeFactory resourceNodeFactory, IAppSettings appSettings, [ImportMany] IEnumerable<Lazy<IDnSpyFileNodeProvider, IDnSpyFileNodeProviderMetadata>> dnSpyFileNodeProviders, [ImportMany] IEnumerable<Lazy<IFileTreeNodeDataFinder, IFileTreeNodeDataFinderMetadata>> mefFinders) {
			this.languageManager = languageManager;
			this.themeManager = themeManager;
			this.fileTreeViewSettings = fileTreeViewSettings;
			this.appSettings = appSettings;

			this.context = new FileTreeNodeDataContext(this, resourceNodeFactory, filter ?? FilterNothingFileTreeNodeFilter.Instance) {
				SyntaxHighlight = fileTreeViewSettings.SyntaxHighlight,
				SingleClickExpandsChildren = fileTreeViewSettings.SingleClickExpandsTreeViewChildren,
				ShowAssemblyVersion = fileTreeViewSettings.ShowAssemblyVersion,
				ShowAssemblyPublicKeyToken = fileTreeViewSettings.ShowAssemblyPublicKeyToken,
				ShowToken = fileTreeViewSettings.ShowToken,
				Language = languageManager.Language,
				UseNewRenderer = appSettings.UseNewRenderer_FileTreeView,
				DeserializeResources = fileTreeViewSettings.DeserializeResources,
				CanDragAndDrop = isGlobal,
			};

			var options = new TreeViewOptions {
				AllowDrop = true,
				IsVirtualizing = true,
				VirtualizationMode = VirtualizationMode.Recycling,
				TreeViewListener = this,
				RootNode = new RootNode {
					DropNodes = OnDropNodes,
					DropFiles = OnDropFiles,
				},
			};
			this.fileTreeNodeGroups = new FileTreeNodeGroups();
			this.dnSpyFileNodeProviders = dnSpyFileNodeProviders.OrderBy(a => a.Metadata.Order).ToArray();
			this.TreeView = treeViewManager.Create(new Guid(TVConstants.FILE_TREEVIEW_GUID), options);
			this.TreeView.SelectionChanged += TreeView_SelectionChanged;
			this.FileManager = fileManager;
			this.DotNetImageManager = dotNetImageManager;
			this.dispatcher = Dispatcher.CurrentDispatcher;
			this.FileManager.SetDispatcher(AddAction);
			fileManager.CollectionChanged += FileManager_CollectionChanged;
			languageManager.LanguageChanged += LanguageManager_LanguageChanged;
			themeManager.ThemeChanged += ThemeManager_ThemeChanged;
			fileTreeViewSettings.PropertyChanged += FileTreeViewSettings_PropertyChanged;
			appSettings.PropertyChanged += AppSettings_PropertyChanged;

			this.WpfCommands = wpfCommandManager.GetCommands(ControlConstants.GUID_FILE_TREEVIEW);

			if (isGlobal) {
				menuManager.InitializeContextMenu((FrameworkElement)this.TreeView.UIObject, new Guid(MenuConstants.GUIDOBJ_FILES_TREEVIEW_GUID), new GuidObjectsProvider(this.TreeView));
				wpfCommandManager.Add(ControlConstants.GUID_FILE_TREEVIEW, (UIElement)TreeView.UIObject);
			}

			this.nodeFinders = mefFinders.OrderBy(a => a.Metadata.Order).ToArray();
			InitializeFileTreeNodeGroups();
		}

		readonly Dispatcher dispatcher;
		internal void AddAction(Action action) {
			if (!dispatcher.HasShutdownFinished && !dispatcher.HasShutdownStarted) {
				bool callInvoke;
				lock (actionsToCall) {
					actionsToCall.Add(action);
					callInvoke = actionsToCall.Count == 1;
				}
				if (callInvoke) {
					// Always notify with a delay because adding stuff to the tree view could
					// cause some problems with the tree view or the list box it derives from.
					dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CallActions));
				}
			}
		}

		// It's not using IDisposable.Dispose() because MEF will call Dispose() at app exit which
		// will trigger code paths that try to call some MEF funcs which will throw since MEF is
		// closing down.
		void IFileTreeView.Dispose() {
			FileManager.CollectionChanged -= FileManager_CollectionChanged;
			languageManager.LanguageChanged -= LanguageManager_LanguageChanged;
			themeManager.ThemeChanged -= ThemeManager_ThemeChanged;
			fileTreeViewSettings.PropertyChanged -= FileTreeViewSettings_PropertyChanged;
			appSettings.PropertyChanged -= AppSettings_PropertyChanged;
			FileManager.Clear();
			TreeView.Root.Children.Clear();
			TreeView.SelectItems(Array.Empty<ITreeNodeData>());
			context.Clear();
		}

		void RefilterNodes() {
			context.FilterVersion++;
			((RootNode)TreeView.Root.Data).Refilter();
		}

		void AppSettings_PropertyChanged(object sender, PropertyChangedEventArgs e) {
			var appSettings = (IAppSettings)sender;
			if (e.PropertyName == nameof(appSettings.UseNewRenderer_FileTreeView)) {
				this.context.UseNewRenderer = appSettings.UseNewRenderer_FileTreeView;
				RefreshNodes();
			}
		}

		void InitializeFileTreeNodeGroups() {
			var orders = new MemberKind[] {
				fileTreeViewSettings.MemberKind0,
				fileTreeViewSettings.MemberKind1,
				fileTreeViewSettings.MemberKind2,
				fileTreeViewSettings.MemberKind3,
				fileTreeViewSettings.MemberKind4,
			};
			fileTreeNodeGroups.SetMemberOrder(orders);
		}

		readonly List<Action> actionsToCall = new List<Action>();

		void CallActions() {
			List<Action> list;
			lock (actionsToCall) {
				list = new List<Action>(actionsToCall);
				actionsToCall.Clear();
			}
			foreach (var a in list)
				a();
		}

		void FileTreeViewSettings_PropertyChanged(object sender, PropertyChangedEventArgs e) {
			var fileTreeViewSettings = (IFileTreeViewSettings)sender;
			switch (e.PropertyName) {
			case nameof(fileTreeViewSettings.SyntaxHighlight):
				context.SyntaxHighlight = fileTreeViewSettings.SyntaxHighlight;
				RefreshNodes();
				break;

			case nameof(fileTreeViewSettings.ShowAssemblyVersion):
				context.ShowAssemblyVersion = fileTreeViewSettings.ShowAssemblyVersion;
				RefreshNodes();
				NotifyNodesTextRefreshed();
				break;

			case nameof(fileTreeViewSettings.ShowAssemblyPublicKeyToken):
				context.ShowAssemblyPublicKeyToken = fileTreeViewSettings.ShowAssemblyPublicKeyToken;
				RefreshNodes();
				NotifyNodesTextRefreshed();
				break;

			case nameof(fileTreeViewSettings.ShowToken):
				context.ShowToken = fileTreeViewSettings.ShowToken;
				RefreshNodes();
				NotifyNodesTextRefreshed();
				break;

			case nameof(fileTreeViewSettings.SingleClickExpandsTreeViewChildren):
				context.SingleClickExpandsChildren = fileTreeViewSettings.SingleClickExpandsTreeViewChildren;
				break;

			case nameof(fileTreeViewSettings.DeserializeResources):
				context.DeserializeResources = fileTreeViewSettings.DeserializeResources;
				break;

			default:
				break;
			}
		}

		public event EventHandler<EventArgs> NodesTextChanged;
		void NotifyNodesTextRefreshed() => NodesTextChanged?.Invoke(this, EventArgs.Empty);
		void ThemeManager_ThemeChanged(object sender, ThemeChangedEventArgs e) => RefreshNodes();
		void LanguageManager_LanguageChanged(object sender, EventArgs e) => UpdateLanguage(((ILanguageManager)sender).Language);

		void UpdateLanguage(ILanguage newLanguage) {
			this.context.Language = newLanguage;
			RefreshNodes();
			RefilterNodes();
			NotifyNodesTextRefreshed();
		}

		void IFileTreeView.SetLanguage(ILanguage language) {
			if (language == null)
				return;
			UpdateLanguage(language);
		}

		public void RefreshNodes(bool showMember, bool memberOrder) {
			if (showMember) {
				RefreshNodes();
				RefilterNodes();
			}
			/*TODO: memberOrder
			Should call InitializeFileTreeNodeGroups(). Some stuff that must be fixed:
			The asm editor has some classes that store indexes of nodes, and would need to be
			updated to just use the normal AddChild() method to restore the node.
			Also, when the asm editor reinserts a node, its children (recursively) must be resorted
			if the sort order has changed.
			*/
		}

		void RefreshNodes() => TreeView.RefreshAllNodes();

		void FileManager_CollectionChanged(object sender, NotifyFileCollectionChangedEventArgs e) {
			switch (e.Type) {
			case NotifyFileCollectionType.Add:
				IDnSpyFileNode newNode;

				var addFileInfo = e.Data as AddFileInfo;
				int index;
				if (addFileInfo != null) {
					newNode = addFileInfo.DnSpyFileNode;
					index = addFileInfo.Index;
					if (newNode.TreeNode == null)
						TreeView.Create(newNode);
				}
				else {
					newNode = CreateNode(null, e.Files[0]);
					TreeView.Create(newNode);
					index = TreeView.Root.Children.Count;
				}

				if ((uint)index >= (uint)TreeView.Root.Children.Count)
					index = TreeView.Root.Children.Count;
				TreeView.Root.Children.Insert(index, newNode.TreeNode);
				CallCollectionChanged(NotifyFileTreeViewCollectionChangedEventArgs.CreateAdd(newNode));
				break;

			case NotifyFileCollectionType.Remove:
				var dict = new Dictionary<IDnSpyFileNode, int>();
				var dict2 = new Dictionary<IDnSpyFile, IDnSpyFileNode>();
				int i = 0;
				foreach (var n in TopNodes) {
					dict[n] = i++;
					dict2[n.DnSpyFile] = n;
				}
				var list = new List<Tuple<IDnSpyFileNode, int>>(e.Files.Select(a => {
					IDnSpyFileNode node;
					bool b = dict2.TryGetValue(a, out node);
					Debug.Assert(b);
					int j = -1;
					b = b && dict.TryGetValue(node, out j);
					Debug.Assert(b);
					return Tuple.Create(node, b ? j : -1);
				}));
				list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
				var removed = new List<IDnSpyFileNode>();
				foreach (var t in list) {
					if (t.Item2 < 0)
						continue;
					Debug.Assert((uint)t.Item2 < (uint)TreeView.Root.Children.Count);
					Debug.Assert(TreeView.Root.Children[t.Item2].Data == t.Item1);
					TreeView.Root.Children.RemoveAt(t.Item2);
					removed.Add(t.Item1);
				}
				DisableMemoryMappedIO(list.Select(a => a.Item1).ToArray());
				CallCollectionChanged(NotifyFileTreeViewCollectionChangedEventArgs.CreateRemove(removed.ToArray()));
				break;

			case NotifyFileCollectionType.Clear:
				var oldNodes = TreeView.Root.Children.Select(a => (IDnSpyFileNode)a.Data).ToArray();
				TreeView.Root.Children.Clear();
				DisableMemoryMappedIO(oldNodes);
				CallCollectionChanged(NotifyFileTreeViewCollectionChangedEventArgs.CreateClear(oldNodes));
				break;

			default:
				Debug.Fail(string.Format("Unknown event type: {0}", e.Type));
				break;
			}
		}

		public void Remove(IEnumerable<IDnSpyFileNode> nodes) => FileManager.Remove(nodes.Select(a => a.DnSpyFile));

		void DisableMemoryMappedIO(IDnSpyFileNode[] nodes) {
			// The nodes will be GC'd eventually, but it's not safe to call Dispose(), so disable
			// mmap'd I/O so the files can at least be modified (eg. deleted) by the user.
			foreach (var node in nodes) {
				foreach (var f in node.DnSpyFile.GetAllChildrenAndSelf())
					MemoryMappedIOHelper.DisableMemoryMappedIO(f);
			}
		}

		public IDnSpyFileNode CreateNode(IDnSpyFileNode owner, IDnSpyFile file) {
			foreach (var provider in dnSpyFileNodeProviders) {
				var result = provider.Value.Create(this, owner, file);
				if (result != null)
					return result;
			}

			return new UnknownFileNode(file);
		}

		void ITreeViewListener.OnEvent(ITreeView treeView, TreeViewListenerEventArgs e) {
			if (e.Event == TreeViewListenerEvent.NodeCreated) {
				Debug.Assert(context != null);
				var node = (ITreeNode)e.Argument;
				var d = node.Data as IFileTreeNodeData;
				if (d != null)
					d.Context = context;
				return;
			}
		}

		public IAssemblyFileNode CreateAssembly(IDnSpyDotNetFile asmFile) =>
			(IAssemblyFileNode)TreeView.Create(new AssemblyFileNode(asmFile)).Data;
		public IModuleFileNode CreateModule(IDnSpyDotNetFile modFile) =>
			(IModuleFileNode)TreeView.Create(new ModuleFileNode(modFile)).Data;
		public IAssemblyReferenceNode Create(AssemblyRef asmRef, ModuleDef ownerModule) =>
			(IAssemblyReferenceNode)TreeView.Create(new AssemblyReferenceNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.AssemblyRefTreeNodeGroupReferences), ownerModule, asmRef)).Data;
		public IModuleReferenceNode Create(ModuleRef modRef) =>
			(IModuleReferenceNode)TreeView.Create(new ModuleReferenceNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.ModuleRefTreeNodeGroupReferences), modRef)).Data;
		public IMethodNode CreateEvent(MethodDef method) =>
			(IMethodNode)TreeView.Create(new MethodNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.MethodTreeNodeGroupEvent), method)).Data;
		public IMethodNode CreateProperty(MethodDef method) =>
			(IMethodNode)TreeView.Create(new MethodNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.MethodTreeNodeGroupProperty), method)).Data;
		public INamespaceNode Create(string name) =>
			(INamespaceNode)TreeView.Create(new NamespaceNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.NamespaceTreeNodeGroupModule), name, new List<TypeDef>())).Data;
		public ITypeNode Create(TypeDef type) =>
			(ITypeNode)TreeView.Create(new TypeNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.TypeTreeNodeGroupNamespace), type)).Data;
		public ITypeNode CreateNested(TypeDef type) =>
			(ITypeNode)TreeView.Create(new TypeNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.TypeTreeNodeGroupType), type)).Data;
		public IMethodNode Create(MethodDef method) =>
			(IMethodNode)TreeView.Create(new MethodNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.MethodTreeNodeGroupType), method)).Data;
		public IPropertyNode Create(PropertyDef property) =>
			(IPropertyNode)TreeView.Create(new PropertyNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.PropertyTreeNodeGroupType), property)).Data;
		public IEventNode Create(EventDef @event) =>
			(IEventNode)TreeView.Create(new EventNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.EventTreeNodeGroupType), @event)).Data;
		public IFieldNode Create(FieldDef field) =>
			(IFieldNode)TreeView.Create(new FieldNode(FileTreeNodeGroups.GetGroup(FileTreeNodeGroupType.FieldTreeNodeGroupType), field)).Data;

		public IFileTreeNodeData FindNode(object @ref) {
			if (@ref == null)
				return null;
			if (@ref is IFileTreeNodeData)
				return (IFileTreeNodeData)@ref;
			if (@ref is IDnSpyFile)
				return FindNode((IDnSpyFile)@ref);
			if (@ref is AssemblyDef)
				return FindNode((AssemblyDef)@ref);
			if (@ref is ModuleDef)
				return FindNode((ModuleDef)@ref);
			if (@ref is ITypeDefOrRef)
				return FindNode(((ITypeDefOrRef)@ref).ResolveTypeDef());
			if (@ref is IMethod && ((IMethod)@ref).MethodSig != null)
				return FindNode(((IMethod)@ref).ResolveMethodDef());
			if (@ref is IField)
				return FindNode(((IField)@ref).ResolveFieldDef());
			if (@ref is PropertyDef)
				return FindNode((PropertyDef)@ref);
			if (@ref is EventDef)
				return FindNode((EventDef)@ref);
			if (@ref is ParamDef)
				return FindNode(((ParamDef)@ref).DeclaringMethod);
			if (@ref is NamespaceRef) {
				var nsRef = (NamespaceRef)@ref;
				return FindNamespaceNode(nsRef.Module, nsRef.Namespace);
			}

			foreach (var finder in nodeFinders) {
				var node = finder.Value.FindNode(this, @ref);
				if (node != null)
					return node;
			}

			return null;
		}

		public IDnSpyFileNode FindNode(IDnSpyFile file) {
			if (file == null)
				return null;
			return Find(TopNodes, file);
		}

		IDnSpyFileNode Find(IEnumerable<IDnSpyFileNode> nodes, IDnSpyFile file) {
			foreach (var n in nodes) {
				if (n.DnSpyFile == file)
					return n;
				if (n.DnSpyFile.Children.Count == 0)
					continue;
				n.TreeNode.EnsureChildrenLoaded();
				var found = Find(n.TreeNode.DataChildren.OfType<IDnSpyFileNode>(), file);
				if (found != null)
					return found;
			}
			return null;
		}

		public IAssemblyFileNode FindNode(AssemblyDef asm) {
			if (asm == null)
				return null;

			foreach (var n in TopNodes.OfType<IAssemblyFileNode>()) {
				if (n.DnSpyFile.AssemblyDef == asm)
					return n;
			}

			return null;
		}

		public IModuleFileNode FindNode(ModuleDef mod) {
			if (mod == null)
				return null;

			foreach (var n in TopNodes.OfType<IAssemblyFileNode>()) {
				n.TreeNode.EnsureChildrenLoaded();
				foreach (var m in n.TreeNode.DataChildren.OfType<IModuleFileNode>()) {
					if (m.DnSpyFile.ModuleDef == mod)
						return m;
				}
			}

			// Check for netmodules
			foreach (var n in TopNodes.OfType<IModuleFileNode>()) {
				if (n.DnSpyFile.ModuleDef == mod)
					return n;
			}

			return null;
		}

		public ITypeNode FindNode(TypeDef td) {
			if (td == null)
				return null;

			var types = new List<TypeDef>();
			for (var t = td; t != null; t = t.DeclaringType)
				types.Add(t);
			types.Reverse();

			var modNode = FindNode(types[0].Module);
			if (modNode == null)
				return null;

			var nsNode = modNode.FindNode(types[0].Namespace);
			if (nsNode == null)
				return null;

			var typeNode = FindNode(nsNode, types[0]);
			if (typeNode == null)
				return null;

			for (int i = 1; i < types.Count; i++) {
				var childNode = FindNode(typeNode, types[i]);
				if (childNode == null)
					return null;
				typeNode = childNode;
			}

			return typeNode;
		}

		ITypeNode FindNode(INamespaceNode nsNode, TypeDef type) {
			if (nsNode == null || type == null)
				return null;

			nsNode.TreeNode.EnsureChildrenLoaded();
			foreach (var n in nsNode.TreeNode.DataChildren.OfType<ITypeNode>()) {
				if (n.TypeDef == type)
					return n;
			}

			return null;
		}

		ITypeNode FindNode(ITypeNode typeNode, TypeDef type) {
			if (typeNode == null || type == null)
				return null;

			typeNode.TreeNode.EnsureChildrenLoaded();
			foreach (var n in typeNode.TreeNode.DataChildren.OfType<ITypeNode>()) {
				if (n.TypeDef == type)
					return n;
			}

			return null;
		}

		public INamespaceNode FindNamespaceNode(IDnSpyFile module, string @namespace) {
			var modNode = FindNode(module) as IModuleFileNode;
			if (modNode != null)
				return modNode.FindNode(@namespace);
			return null;
		}

		public IMethodNode FindNode(MethodDef md) {
			if (md == null)
				return null;

			var typeNode = FindNode(md.DeclaringType);
			if (typeNode == null)
				return null;

			typeNode.TreeNode.EnsureChildrenLoaded();
			foreach (var n in typeNode.TreeNode.DataChildren.OfType<IMethodNode>()) {
				if (n.MethodDef == md)
					return n;
			}

			foreach (var n in typeNode.TreeNode.DataChildren.OfType<IPropertyNode>()) {
				n.TreeNode.EnsureChildrenLoaded();
				foreach (var m in n.TreeNode.DataChildren.OfType<IMethodNode>()) {
					if (m.MethodDef == md)
						return m;
				}
			}

			foreach (var n in typeNode.TreeNode.DataChildren.OfType<IEventNode>()) {
				n.TreeNode.EnsureChildrenLoaded();
				foreach (var m in n.TreeNode.DataChildren.OfType<IMethodNode>()) {
					if (m.MethodDef == md)
						return m;
				}
			}

			return null;
		}

		public IFieldNode FindNode(FieldDef fd) {
			if (fd == null)
				return null;

			var typeNode = FindNode(fd.DeclaringType);
			if (typeNode == null)
				return null;

			typeNode.TreeNode.EnsureChildrenLoaded();
			foreach (var n in typeNode.TreeNode.DataChildren.OfType<IFieldNode>()) {
				if (n.FieldDef == fd)
					return n;
			}

			return null;
		}

		public IPropertyNode FindNode(PropertyDef pd) {
			if (pd == null)
				return null;

			var typeNode = FindNode(pd.DeclaringType);
			if (typeNode == null)
				return null;

			typeNode.TreeNode.EnsureChildrenLoaded();
			foreach (var n in typeNode.TreeNode.DataChildren.OfType<IPropertyNode>()) {
				if (n.PropertyDef == pd)
					return n;
			}

			return null;
		}

		public IEventNode FindNode(EventDef ed) {
			if (ed == null)
				return null;

			var typeNode = FindNode(ed.DeclaringType);
			if (typeNode == null)
				return null;

			typeNode.TreeNode.EnsureChildrenLoaded();
			foreach (var n in typeNode.TreeNode.DataChildren.OfType<IEventNode>()) {
				if (n.EventDef == ed)
					return n;
			}

			return null;
		}

		public IEnumerable<IModuleFileNode> GetAllModuleNodes() {
			foreach (var node in TopNodes) {
				var modNode = node as IModuleFileNode;
				if (modNode != null) {
					yield return modNode;
					continue;
				}

				var asmNode = node as IAssemblyFileNode;
				if (asmNode != null) {
					asmNode.TreeNode.EnsureChildrenLoaded();
					foreach (var c in asmNode.TreeNode.DataChildren) {
						modNode = c as IModuleFileNode;
						if (modNode != null)
							yield return modNode;
					}
					continue;
				}
			}
		}

		public IEnumerable<IDnSpyFileNode> GetAllCreatedDnSpyFileNodes() {
			foreach (var n in GetAllCreatedDnSpyFileNodes(TopNodes))
				yield return n;
		}

		IEnumerable<IDnSpyFileNode> GetAllCreatedDnSpyFileNodes(IEnumerable<ITreeNodeData> nodes) {
			foreach (var n in nodes) {
				var fn = n as IDnSpyFileNode;
				if (fn != null) {
					yield return fn;
					// Don't call fn.TreeNode.EnsureChildrenLoaded(), only return created nodes
					foreach (var c in GetAllCreatedDnSpyFileNodes(fn.TreeNode.DataChildren))
						yield return c;
				}
			}
		}

		public void AddNode(IDnSpyFileNode fileNode, int index) {
			if (fileNode == null)
				throw new ArgumentNullException(nameof(fileNode));
			Debug.Assert(!TreeView.Root.DataChildren.Contains(fileNode));
			Debug.Assert(fileNode.TreeNode.Parent == null);
			FileManager.ForceAdd(fileNode.DnSpyFile, false, new AddFileInfo(fileNode, index));
			Debug.Assert(TreeView.Root.DataChildren.Contains(fileNode));
		}

		sealed class AddFileInfo {
			public readonly IDnSpyFileNode DnSpyFileNode;
			public readonly int Index;

			public AddFileInfo(IDnSpyFileNode fileNode, int index) {
				this.DnSpyFileNode = fileNode;
				this.Index = index;
			}
		}

		void OnDropNodes(int index, int[] nodeIndexes) {
			if (!context.CanDragAndDrop)
				return;

			nodeIndexes = nodeIndexes.Distinct().ToArray();
			if (nodeIndexes.Length == 0)
				return;

			var children = TreeView.Root.Children;
			if ((uint)index > children.Count)
				return;

			var insertNode = index == children.Count ? null : children[index];

			var movedNodes = new List<ITreeNode>();
			Array.Sort(nodeIndexes, (a, b) => b.CompareTo(a));
			for (int i = 0; i < nodeIndexes.Length; i++) {
				var j = nodeIndexes[i];
				if ((uint)j >= children.Count)
					continue;
				movedNodes.Add(children[j]);
				children.RemoveAt(j);
			}
			movedNodes.Reverse();
			if (movedNodes.Count == 0)
				return;

			int insertIndex = children.IndexOf(insertNode);
			if (insertIndex < 0)
				insertIndex = children.Count;
			for (int i = 0; i < movedNodes.Count; i++)
				children.Insert(insertIndex + i, movedNodes[i]);

			TreeView.SelectItems(movedNodes.Select(a => a.Data));
		}

		void OnDropFiles(int index, string[] filenames) {
			if (!context.CanDragAndDrop)
				return;

			var origFilenames = filenames;
			var existingFiles = new HashSet<string>(FileManager.GetFiles().Select(a => a.Filename ?? string.Empty), StringComparer.OrdinalIgnoreCase);
			filenames = filenames.Where(a => File.Exists(a) && !existingFiles.Contains(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
			ITreeNodeData newSelectedNode = null;
			for (int i = 0, j = 0; i < filenames.Length; i++) {
				var file = FileManager.TryCreateOnly(DnSpyFileInfo.CreateFile(filenames[i]));
				if (file == null)
					continue;
				var node = CreateNode(null, file);
				FileManager.ForceAdd(file, false, new AddFileInfo(node, index + j++));
				if (newSelectedNode == null)
					newSelectedNode = node;
			}
			if (newSelectedNode == null) {
				var filename = origFilenames.FirstOrDefault(a => File.Exists(a));
				if (filename != null) {
					var key = new FilenameKey(filename);
					var file = FileManager.GetFiles().FirstOrDefault(a => key.Equals(a.Key));
					newSelectedNode = FindNode(file);
				}
			}
			if (newSelectedNode != null)
				TreeView.SelectItems(new[] { newSelectedNode });
		}

		IDnSpyFileNode[] GetNewSortedNodes() {
			var origOrder = TopNodes.ToArray();
			var files = origOrder.Select((a, i) => Tuple.Create(a, i)).ToList();
			files.Sort(DnSpyFileNodeComparer.Instance);
			var sorted = files.Select(a => a.Item1).ToArray();
			if (Equals(sorted, origOrder))
				return null;
			return sorted;
		}

		public bool CanSortTopNodes => GetNewSortedNodes() != null;

		public void SortTopNodes() {
			var sortedFiles = GetNewSortedNodes();
			if (sortedFiles == null)
				return;

			var selectedNodes = TreeView.SelectedItems;
			var old = disable_SelectionChanged;
			try {
				disable_SelectionChanged = true;
				TreeView.Root.Children.Clear();
				foreach (var n in sortedFiles)
					TreeView.Root.Children.Add(n.TreeNode);
				TreeView.SelectItems(selectedNodes);
			}
			finally {
				disable_SelectionChanged = old;
			}
		}

		static bool Equals(IList<IDnSpyFileNode> a, IList<IDnSpyFileNode> b) {
			if (a.Count != b.Count)
				return false;
			for (int i = 0; i < a.Count; i++) {
				if (a[i] != b[i])
					return false;
			}
			return true;
		}

		sealed class DnSpyFileNodeComparer : IComparer<Tuple<IDnSpyFileNode, int>> {
			public static readonly DnSpyFileNodeComparer Instance = new DnSpyFileNodeComparer();

			public int Compare(Tuple<IDnSpyFileNode, int> x, Tuple<IDnSpyFileNode, int> y) {
				if (x == y)
					return 0;
				if (x == null)
					return -1;
				if (y == null)
					return 1;
				int c = GetIsAutoLoadedOrder(x.Item1.DnSpyFile.IsAutoLoaded).CompareTo(GetIsAutoLoadedOrder(y.Item1.DnSpyFile.IsAutoLoaded));
				if (c != 0)
					return c;
				c = StringComparer.InvariantCultureIgnoreCase.Compare(x.ToString(), y.ToString());
				if (c != 0)
					return c;
				return x.Item2.CompareTo(y.Item2);
			}

			static int GetIsAutoLoadedOrder(bool b) => b ? 1 : 0;
		}
	}
}
