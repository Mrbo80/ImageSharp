﻿// <copyright file="Point.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageSharp
{
    using System;
    using System.ComponentModel;
    using System.Numerics;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Represents an ordered pair of integer x- and y-coordinates that defines a point in
    /// a two-dimensional plane.
    /// </summary>
    /// <remarks>
    /// This struct is fully mutable. This is done (against the guidelines) for the sake of performance,
    /// as it avoids the need to create new values for modification operations.
    /// </remarks>
    public struct Point : IEquatable<Point>
    {
        /// <summary>
        /// Represents a <see cref="Point"/> that has X and Y values set to zero.
        /// </summary>
        public static readonly Point Empty = default(Point);

        /// <summary>
        /// Initializes a new instance of the <see cref="Point"/> struct.
        /// </summary>
        /// <param name="value">The horizontal and vertical position of the point.</param>
        public Point(int value)
            : this()
        {
            this.X = LowInt16(value);
            this.Y = HighInt16(value);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Point"/> struct.
        /// </summary>
        /// <param name="x">The horizontal position of the point.</param>
        /// <param name="y">The vertical position of the point.</param>
        public Point(int x, int y)
            : this()
        {
            this.X = x;
            this.Y = y;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Point"/> struct from the given <see cref="Size"/>.
        /// </summary>
        /// <param name="size">The size</param>
        public Point(Size size)
        {
            this.X = size.Width;
            this.Y = size.Height;
        }

        /// <summary>
        /// Gets or sets the x-coordinate of this <see cref="Point"/>.
        /// </summary>
        public int X { get; set; }

        /// <summary>
        /// Gets or sets the y-coordinate of this <see cref="Point"/>.
        /// </summary>
        public int Y { get; set; }

        /// <summary>
        /// Gets a value indicating whether this <see cref="Point"/> is empty.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool IsEmpty => this.Equals(Empty);

        /// <summary>
        /// Creates a <see cref="PointF"/> with the coordinates of the specified <see cref="Point"/>.
        /// </summary>
        /// <param name="point">The point</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PointF(Point point)
        {
            return new PointF(point.X, point.Y);
        }

        /// <summary>
        /// Creates a <see cref="Vector2"/> with the coordinates of the specified <see cref="Point"/>.
        /// </summary>
        /// <param name="point">The point</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Vector2(Point point)
        {
            return new Vector2(point.X, point.Y);
        }

        /// <summary>
        /// Creates a <see cref="Size"/> with the coordinates of the specified <see cref="Point"/>.
        /// </summary>
        /// <param name="point">The point</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator Size(Point point)
        {
            return new Size(point.X, point.Y);
        }

        /// <summary>
        /// Translates a <see cref="Point"/> by a given <see cref="Size"/>.
        /// </summary>
        /// <param name="point">The point on the left hand of the operand.</param>
        /// <param name="size">The size on the right hand of the operand.</param>
        /// <returns>
        /// The <see cref="Point"/>
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point operator +(Point point, Size size)
        {
            return Add(point, size);
        }

        /// <summary>
        /// Translates a <see cref="Point"/> by the negative of a given <see cref="Size"/>.
        /// </summary>
        /// <param name="point">The point on the left hand of the operand.</param>
        /// <param name="size">The size on the right hand of the operand.</param>
        /// <returns>The <see cref="Point"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point operator -(Point point, Size size)
        {
            return Subtract(point, size);
        }

        /// <summary>
        /// Compares two <see cref="Point"/> objects for equality.
        /// </summary>
        /// <param name="left">The <see cref="Point"/> on the left side of the operand.</param>
        /// <param name="right">The <see cref="Point"/> on the right side of the operand.</param>
        /// <returns>
        /// True if the current left is equal to the <paramref name="right"/> parameter; otherwise, false.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Point left, Point right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Compares two <see cref="Point"/> objects for inequality.
        /// </summary>
        /// <param name="left">The <see cref="Point"/> on the left side of the operand.</param>
        /// <param name="right">The <see cref="Point"/> on the right side of the operand.</param>
        /// <returns>
        /// True if the current left is unequal to the <paramref name="right"/> parameter; otherwise, false.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Point left, Point right)
        {
            return !left.Equals(right);
        }

        /// <summary>
        /// Translates a <see cref="Point"/> by the negative of a given <see cref="Size"/>.
        /// </summary>
        /// <param name="point">The point on the left hand of the operand.</param>
        /// <param name="size">The size on the right hand of the operand.</param>
        /// <returns>The <see cref="Point"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point Add(Point point, Size size)
        {
            return new Point(point.X + size.Width, point.Y + size.Height);
        }

        /// <summary>
        /// Translates a <see cref="Point"/> by the negative of a given <see cref="Size"/>.
        /// </summary>
        /// <param name="point">The point on the left hand of the operand.</param>
        /// <param name="size">The size on the right hand of the operand.</param>
        /// <returns>The <see cref="Point"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point Subtract(Point point, Size size)
        {
            return new Point(point.X - size.Width, point.Y - size.Height);
        }

        /// <summary>
        /// Converts a <see cref="PointF"/> to a <see cref="Point"/> by performing a ceiling operation on all the coordinates.
        /// </summary>
        /// <param name="point">The point</param>
        /// <returns>The <see cref="Point"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point Ceiling(PointF point)
        {
            return new Point((int)MathF.Ceiling(point.X), (int)MathF.Ceiling(point.Y));
        }

        /// <summary>
        /// Converts a <see cref="PointF"/> to a <see cref="Point"/> by performing a round operation on all the coordinates.
        /// </summary>
        /// <param name="point">The point</param>
        /// <returns>The <see cref="Point"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point Round(PointF point)
        {
            return new Point((int)MathF.Round(point.X), (int)MathF.Round(point.Y));
        }

        /// <summary>
        /// Converts a <see cref="Vector2"/> to a <see cref="Point"/> by performing a round operation on all the coordinates.
        /// </summary>
        /// <param name="vector">The vector</param>
        /// <returns>The <see cref="Point"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point Round(Vector2 vector)
        {
            return new Point((int)MathF.Round(vector.X), (int)MathF.Round(vector.Y));
        }

        /// <summary>
        /// Rotates a point around the given rotation matrix.
        /// </summary>
        /// <param name="point">The point to rotate</param>
        /// <param name="rotation">Rotation matrix used</param>
        /// <returns>The rotated <see cref="Point"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point Rotate(Point point, Matrix3x2 rotation)
        {
            return Round(Vector2.Transform(new Vector2(point.X, point.Y), rotation));
        }

        /// <summary>
        /// Skews a point using the given skew matrix.
        /// </summary>
        /// <param name="point">The point to rotate</param>
        /// <param name="skew">Rotation matrix used</param>
        /// <returns>The rotated <see cref="Point"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Point Skew(Point point, Matrix3x2 skew)
        {
            return Round(Vector2.Transform(new Vector2(point.X, point.Y), skew));
        }

        /// <summary>
        /// Gets a <see cref="Vector2"/> representation for this <see cref="Point"/>.
        /// </summary>
        /// <returns>A <see cref="Vector2"/> representation for this object.</returns>
        public Vector2 ToVector2()
        {
            return new Vector2(this.X, this.Y);
        }

        /// <summary>
        /// Translates this <see cref="Point"/> by the specified amount.
        /// </summary>
        /// <param name="dx">The amount to offset the x-coordinate.</param>
        /// <param name="dy">The amount to offset the y-coordinate.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Offset(int dx, int dy)
        {
            this.X += dx;
            this.Y += dy;
        }

        /// <summary>
        /// Translates this <see cref="Point"/> by the specified amount.
        /// </summary>
        /// <param name="point">The <see cref="Point"/> used offset this <see cref="Point"/>.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Offset(Point point)
        {
            this.Offset(point.X, point.Y);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            return this.GetHashCode(this);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            if (this.IsEmpty)
            {
                return "Point [ Empty ]";
            }

            return $"Point [ X={this.X}, Y={this.Y} ]";
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            if (obj is Point)
            {
                return this.Equals((Point)obj);
            }

            return false;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(Point other)
        {
            return this.X == other.X && this.Y == other.Y;
        }

        private static short HighInt16(int n) => unchecked((short)((n >> 16) & 0xffff));

        private static short LowInt16(int n) => unchecked((short)(n & 0xffff));

        private int GetHashCode(Point point)
        {
            unchecked
            {
                return point.X ^ point.Y;
            }
        }
    }
}