package org.splortch.mediaserver.android

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button as MaterialButton
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import org.splortch.mediaserver.core.AppTheme

internal object MediaColors {
    val Cyan = Color(0xFF00B8FF)
    val Mint = Color(0xFF2BE0A0)
    val Lime = Color(0xFFC6FF3D)
    val Yellow = Color(0xFFFFE53B)
    val Ink = Color(0xFF061A23)
    val Gradient = listOf(Cyan, Mint, Lime, Yellow)
}

@Composable
internal fun MediaTheme(theme: AppTheme = AppTheme.Dark, content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme =
            when (theme) {
                AppTheme.Light ->
                    lightColorScheme(
                        primary = Color(0xFF00695C),
                        onPrimary = Color.White,
                        primaryContainer = Color(0xFFB6F5DB),
                        onPrimaryContainer = Color(0xFF002019),
                        secondary = Color(0xFF00658A),
                        onSecondary = Color.White,
                        secondaryContainer = Color(0xFFC5EAFF),
                        onSecondaryContainer = Color(0xFF001E2C),
                        tertiary = Color(0xFF496800),
                        onTertiary = Color.White,
                        tertiaryContainer = Color(0xFFDEFCA4),
                        onTertiaryContainer = Color(0xFF121F00),
                        background = Color(0xFFF0F8FC),
                        onBackground = Color(0xFF092632),
                        surface = Color.White,
                        onSurface = Color(0xFF092632),
                        surfaceVariant = Color(0xFFE0EEF3),
                        onSurfaceVariant = Color(0xFF3A5261),
                        outline = Color(0xFF5F7784),
                        outlineVariant = Color(0xFFB7CBD5),
                    )
                AppTheme.Dark ->
                    darkColorScheme(
                        primary = MediaColors.Mint,
                        onPrimary = MediaColors.Ink,
                        primaryContainer = Color(0xFF163B3B),
                        onPrimaryContainer = Color(0xFFB5FFDB),
                        secondary = MediaColors.Cyan,
                        onSecondary = MediaColors.Ink,
                        secondaryContainer = Color(0xFF12394C),
                        onSecondaryContainer = Color(0xFFB9EFFF),
                        tertiary = MediaColors.Lime,
                        onTertiary = MediaColors.Ink,
                        tertiaryContainer = Color(0xFF2D3C12),
                        onTertiaryContainer = Color(0xFFE1FFA3),
                        background = Color(0xFF080F17),
                        onBackground = Color(0xFFEFFBFF),
                        surface = Color(0xFF111F2B),
                        onSurface = Color(0xFFEFFBFF),
                        surfaceVariant = Color(0xFF203442),
                        onSurfaceVariant = Color(0xFFABC2CE),
                        outline = Color(0xFF728E9D),
                        outlineVariant = Color(0xFF304958),
                    )
            },
        shapes =
            Shapes(
                small = RoundedCornerShape(12.dp),
                medium = RoundedCornerShape(18.dp),
                large = RoundedCornerShape(24.dp),
            ),
        content = content,
    )
}

@Composable
internal fun GradientButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable RowScope.() -> Unit,
) {
    val colors =
        if (enabled) MediaColors.Gradient
        else
            listOf(
                MaterialTheme.colorScheme.surfaceVariant,
                MaterialTheme.colorScheme.surfaceVariant,
            )
    MaterialButton(
        onClick = onClick,
        modifier = modifier.background(Brush.linearGradient(colors), MaterialTheme.shapes.small),
        enabled = enabled,
        shape = MaterialTheme.shapes.small,
        colors =
            ButtonDefaults.buttonColors(
                containerColor = Color.Transparent,
                contentColor = MediaColors.Ink,
                disabledContainerColor = Color.Transparent,
                disabledContentColor = MaterialTheme.colorScheme.onSurfaceVariant,
            ),
        content = content,
    )
}
