// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

using idunno.AtProto;

namespace JerryChart.Monitor;

internal sealed record Hit(DateTimeOffset CreatedAt, AtUri AtUri, Did AuthorDid, Did ParentAuthorDid, AtUri ParentAtUri);