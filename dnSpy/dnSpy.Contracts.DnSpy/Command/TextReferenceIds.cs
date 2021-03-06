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

namespace dnSpy.Contracts.Command {
	/// <summary>
	/// Reference IDs
	/// </summary>
	public enum TextReferenceIds {
		/// <summary>
		/// Move the caret to the next reference
		/// </summary>
		MoveToNextReference,

		/// <summary>
		/// Move the caret to the previous reference
		/// </summary>
		MoveToPreviousReference,

		/// <summary>
		/// Move the caret to the next definition
		/// </summary>
		MoveToNextDefinition,

		/// <summary>
		/// Move the caret to the previous definition
		/// </summary>
		MoveToPreviousDefinition,

		/// <summary>
		/// Move the caret to the definition the reference references
		/// </summary>
		FollowReference,

		/// <summary>
		/// Move the caret to the definition the reference references, use a new tab
		/// </summary>
		FollowReferenceNewTab,

		/// <summary>
		/// Moves the caret to the matching brace
		/// </summary>
		MoveToMatchingBrace,

		/// <summary>
		/// Moves the caret to the matching brace, select span
		/// </summary>
		MoveToMatchingBraceSelect,
	}
}
