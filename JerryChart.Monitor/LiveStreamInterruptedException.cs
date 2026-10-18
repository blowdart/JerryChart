// Copyright (c) Barry Dorrans. All rights reserved.
// Licensed under the MIT License.

namespace JerryChart.Monitor;

internal sealed class LiveStreamInterruptedException(Exception? innerException)
    : IOException("The live Jetstream stream ended or its transport was interrupted.", innerException);
