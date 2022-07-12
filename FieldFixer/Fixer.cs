﻿/*
 * Created by SharpDevelop.
 * User: User
 * Date: 01.04.2022
 * Time: 21:57
 * 
 * To change this template use Tools | Options | Coding | Edit Standard Headers.
 */
using System;
using System.Collections.Generic;
using System.IO;
using Mono.Cecil;
using System.Linq;

namespace FieldFixer
{
	/// <summary>
	/// Description of AssemblyParser.
	/// </summary>
	public class Fixer
	{
		public Dictionary<uint, Tuple<uint, string>> cmdid_mapping = null;
		
		private const string token_attrib_name = "TokenAttribute";
		
		private string obf_base_class_name = null;
		private string de_base_class_name = "MessageBase";
		
		private string de_cmd_id_name = "CmdId";
		
		AssemblyDefinition obf_assembly = null;
		AssemblyDefinition de_assembly = null;
		
		Dictionary<uint, TypeDefinition> obf_types = null;
		Dictionary<uint, TypeDefinition> de_types = null;
		
		Dictionary<string,string> obf_map = null;
		
		private int debug_level = 0;
		
		public Fixer()
		{
			obf_map = new Dictionary<string, string>();
		}
		
		public void save_nt(string filename) {			
			const string tmpl = "{0}⇨{1}";
			
			var writer = new StreamWriter(filename);
			
			writer.WriteLine("# Autogenerated file, do not edit");
			writer.WriteLine("# Created at {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
			
			writer.WriteLine(tmpl, obf_base_class_name, de_base_class_name);
			
			foreach (var kvp in obf_map) {
				writer.WriteLine(tmpl, kvp.Key, kvp.Value);
			}
			
			writer.Close();
		}
		
		public void AddNTEntry(string obf, string de) {
			if (obf == de)
				return;
			
			if (!obf.IsBeeObfuscated())
				return;
			
			try {
				obf_map.Add(obf, de);
			} catch (ArgumentException e) {
				if (obf_map[obf] == de) {
					// Do nothing
				} else {
					WriteLine(string.Format("WARN: Adding {0} as a value for {1} failed, it's been already mapped to {2}",
					                                                  de, obf, obf_map[obf]));
				}
			}
		}
		
		public void load_cmdid_mapping(string filename) {
			cmdid_mapping = new Dictionary<uint, Tuple<uint, string>>();
			
			StreamReader sr = new StreamReader(filename);
			string line;
			while ((line = sr.ReadLine()) != null) {
				var row = line.Split(',');
				
				var name = row[0].Trim();
				var old_cmdid_s = row[1].Trim();
				var new_cmdid_s = row[2].Trim();
				
				if (string.IsNullOrEmpty(old_cmdid_s) || string.IsNullOrEmpty(new_cmdid_s) || string.IsNullOrEmpty(name))
					continue; // Skip unmapped packets
				
				var old_cmdid = UInt32.Parse(old_cmdid_s);
				var new_cmdid = UInt32.Parse(new_cmdid_s);
				
				var val = new Tuple<uint,string>(new_cmdid, name);
				
				WriteLine("{0} {1}", old_cmdid, val);
				
				cmdid_mapping.Add(old_cmdid, val);
			}
			
			WriteLine("Loaded mappings for {0} packet types", cmdid_mapping.Count);
		}
		
		private Dictionary<uint, TypeDefinition> load_pb_packet_types_from_assembly(AssemblyDefinition asm, string pb_base_class, string pb_cmd_id) {			
			var types = GetProtobufPacketTypes(asm, pb_base_class, pb_cmd_id);
			
			var types_dict = new Dictionary<uint, TypeDefinition>();
			
			foreach (var type in types)
			{
				var cmd_id = GetCmdId(type, pb_cmd_id);
				//var token = GetToken(type);
				try {
					types_dict.Add(cmd_id, type);
				} catch (ArgumentException e) {
					var en = GetProtobufPacketEnum(type, pb_base_class, pb_cmd_id);
					// TODO: dirty hack!
					var constants = en.Fields.Where(f => f.HasConstant).Select(f => f.GetConstant()).ToList();
					var is_debug = constants.Contains(2);
					if (is_debug) {
						// This is DebugNotify packet, ignore it
					} else {
						// DebugNotify came first, we need to overwrite it
						types_dict[cmd_id] = type;
					}
				}
			}
			
			return types_dict;
		}
		
		private void map_regular_field(RegularField ob, RegularField de, bool map_constants = true) {
			var r_ob_t = ob.field_type;
			var r_de_t = de.field_type;
			
			var d_ob_t = ob.field_number.DeclaringType.Name;
			var d_de_t = de.field_number.DeclaringType.Name;
						
			if (!map_type_handling_generics(r_ob_t, r_de_t)) {
				WriteLine("WARN: fields {0}.{1} and {2}.{3} have incompatible types: {4} and {5}! Skipping mapping", 
				          d_ob_t, ob.name, 
				          d_de_t, de.name, 
				          r_ob_t, r_de_t);
			} else {
				if (map_constants) {
					AssignConstant(de.field_number, ob.field_number.Resolve().Constant);
					AddNTEntry(ob.name, de.name);
				}
			}
		}
		
		private bool map_type_handling_generics(TypeReference obf, TypeReference de) {			
			if (obf.IsGenericInstance != de.IsGenericInstance) {
				WriteLine("Types {0} and {1} have different generality: {2} and {3}! Skipping mapping", obf, de, obf.IsGenericInstance, de.IsGenericInstance);
				return false;
			}
			
			if (obf.IsGenericInstance) {
				var g_obf = obf as GenericInstanceType;
				var g_de = de as GenericInstanceType;
				
				var obf_args = g_obf.GenericArguments;
				var de_args = g_de.GenericArguments;
				
				if (obf_args.Count != de_args.Count) {
					WriteLine("Types {0} and {1} have different number of generic parameters: {2} and {3}! Skipping mapping",
					                  obf, de, obf_args.Count, de_args.Count);
					return false;
				} else {
					bool mapped = true;
					
					for (int i = 0; i < obf_args.Count; i++) {
						mapped &= map_type_handling_generics(obf_args[i], de_args[i]);
					}
					
					AddNTEntry(g_obf.Name, g_de.Name);
					
					return mapped;
				}
			} else {
				// Regular types, map them
				var obf_t = obf.Resolve();
				var de_t = de.Resolve();
				
				if (obf_t == null || de_t == null) {
					WriteLine("Poop");
				}
				
				var is_o_pb = IsProtobufType(obf_t, obf_base_class_name);
				var is_d_pb = IsProtobufType(de_t, de_base_class_name);
				
				if (is_o_pb != is_d_pb) {
					WriteLine("Types {0} and {1} have different protobufality: {2} and {3}! Skipping mapping", obf, de, is_o_pb, is_d_pb);
					return false;
				}
				
				if (is_o_pb) {
					// Only map them if they are protobuf decendats
					map_protobuf_type(obf, de);
				}
				
				if (obf_t.IsEnum != de_t.IsEnum) {
					WriteLine("Types {0} and {1} have different IsEnum: {2} and {3}! Skipping mapping", obf, de, obf_t.IsEnum, de_t.IsEnum);
					return false;
				}
				
				if (obf_t.IsEnum && de_t.IsEnum) {
					map_enums(obf_t, de_t);
				}
				
				AddNTEntry(obf.Name, de.Name);
				
				return true;
			}
		}
		
		private void map_enums(TypeDefinition obf, TypeDefinition de) {
			var length = Math.Min(obf.Fields.Count, de.Fields.Count);
			
			for (int i = 0; i < length; i++) {
				var o_e = obf.Fields[i];
				var d_e = de.Fields[i];
				
				if (!object.Equals(o_e.Constant, d_e.Constant)) {
					WriteLine("Enum elements {0} and {1} have different constants: {2} and {3}! Skipping mapping",
					         o_e.Name, d_e.Name, o_e.Constant, d_e.Constant);
					return;
				}
				
				AddNTEntry(o_e.Name, d_e.Name);
			}
		}
		
		private void map_protobuf_type(TypeReference obf, TypeReference de, bool map_constants = true) {			
			var obf_fields = get_protobuf_fields(obf);
			var de_fields = get_protobuf_fields(de);
			
			//WriteLine("{0}({1}) => {2}({3})", obf.Name,obf_fields.Count, de.Name, de_fields.Count);
			
			uint field_count = (uint)de_fields.Count;
			debug_level++;
			if (obf_fields.Count < de_fields.Count) {
				WriteLine("WARN: Number of fields in {0} is less than in {1}; possible incorrect mapping!", obf.Name, de.Name);
				field_count = (uint)obf_fields.Count;
			}
			
			AddNTEntry(obf.Name, de.Name);
			
			for (uint i = 0; i < field_count; i++) {
				var ob_field = obf_fields[i];
				var de_field = de_fields[i];
				
				var ob_field_v = ob_field.GetType();
				var de_field_v = de_field.GetType();
				
				if (!ob_field_v.Equals(de_field_v)) {
					WriteLine("WARN: fields {0}.{1} and {2}.{3} have incompatible variety: {4} and {5}! Skipping mapping", 
					          obf.Name, ob_field.name, 
					          de.Name, de_field.name, 
					          ob_field_v, de_field_v);
				} else {
					if (ob_field_v.Equals(typeof(RegularField))) {
						// Just regular field; assign new constant and map type recursively
						var r_ob_field = ob_field as RegularField;
						var r_de_field = de_field as RegularField;
						
						map_regular_field(r_ob_field, r_de_field, map_constants);
					} else {
						var o_ob_field = ob_field as OneofField;
						var o_de_field = de_field as OneofField;
						
						AddNTEntry(o_ob_field.name, o_de_field.name);
						
						int entry_count = o_de_field.variants.Count;
						
						if (o_ob_field.variants.Count < o_de_field.variants.Count) {
							WriteLine("WARN: older OneOf field {0} have {1} entries, but newer {2} have less: {3}! Possible incorrect mapping", 
							                  ob_field.name, o_ob_field.variants.Count,
							                  de_field.name, o_de_field.variants.Count);
							entry_count = o_ob_field.variants.Count;
						}
						
						for (int j = 0; j < entry_count; j++) {
							map_regular_field(o_ob_field.variants[j], o_de_field.variants[j], map_constants);
						}
					}
				}
			}
			debug_level--;
		}
		
		private SortedDictionary<uint, ProtobufField> get_protobuf_fields(TypeReference t) {	
			var type = t.Resolve();
			
			// Enumerate all fields and select pairs public-private (id - value) with token for IDs
			var result = new SortedDictionary<uint, ProtobufField>();
			
			var fields = type.Fields.OrderBy(x => GetToken(x)).ToList();
			
			if (fields.Count == 0)
				return result; // Nothing to do here
			
			//uint base_token = fields.Select(f => GetFieldToken(f)).Min(); // Min will throw if collection is empty, but we checked it just above
			
			int prop_seq_number = 0;
			uint field_seq_number = 0;
			
			var properties = type.Properties.Where(p => p.HasThis /* not static */ && !p.GetMethod.IsVirtual /* doesn't have 'override' spec */).OrderBy(x => GetToken(x)).ToList();
			
			// For oneofs: find all corresponding enums
			// See comment about oneofs below
			List<TypeDefinition> oneof_enums = new List<TypeDefinition>();
			for (int i = 0; i < fields.Count; i++) {
				var f = fields[i];
				if (f.FieldType.FullName.Equals(typeof(object).FullName)) {
					var en_f = fields[i+1];
					var en = en_f.FieldType.Resolve();
					if (!en.IsEnum)
						throw new ArgumentException(string.Format("Your assumption is fucked up: type {0}, subtype {1}", type.Name, en.Name));
					oneof_enums.Add(en);
					i++;
				}
			}
			
			int current_oneof = 0;
			
			for (int i = 0; i < fields.Count-1; i++) {
				var f1 = fields[i];
				
				if (is_protobuf_field(f1)) {
					// Protobuf ID
					var f2 = fields[i+1];
					
					// This hack is a workaround for corner-case: oneof with one element is last in the list of fields
					var hack = (current_oneof == oneof_enums.Count-1) &&
						f2.FieldType.FullName.Equals(typeof(object).FullName) &&
						(prop_seq_number == properties.Count-2);
					
					if (f2.IsPrivate && !f2.HasConstant && !hack) {
						// Regular field
						var ft = f2.FieldType;
						
						if (ft.IsGenericInstance) {
							i++; // There're two fields of generic type, one is FieldCodec, the other one is actual data
						}
						
						result.Add(field_seq_number++, new RegularField(f1, ft));
						i++;
						prop_seq_number++;
					} else if (is_protobuf_field(f2) || hack) {
						// And now it's time for some BLACK MAGIC
						// Sequence of protobuf fields that follow each other mean without delimiting type fields OneOf field
						// It is followed by the regular field, so we can't just read everything sequentially
						// But this "oneof" has:
						// 1. Corresponding "object" field (no other fields has that).
						// 2. Corresponding field with enum type right after this "object" field.
						// So we should find corresponding enum (just by index) and read as many values as there's items in the enum
						// This enum's values also seem to be unobfuscated (lucky us!)
						// Read all variants
						var oneof_enum = oneof_enums[current_oneof++];
						var oneof_variants = new List<FieldDefinition>();
						for (int j = 0; j < oneof_enum.Fields.Count-2; j++) // First field is '__value', next is 'None'
						{
							oneof_variants.Add(fields[i+j]);
						}						
						
						# if false
						// We can guess the types of elements in the following way:
						// 1. Find a property linked to this particular field - it has the same type
						int idx = -1;
						
						for (int j = 0; j < properties.Count; j++) {
							if (properties[j].PropertyType.Equals(oneof_enum)) {
							    idx = j;
							    break;
							}
						}
						
						if (idx < 0)
							throw new ArgumentException(string.Format("Failed to find property for oneof {0} (type {1})", oneof_enum.Name, type.Name));
						
						//WriteLine("Enum: {0}", oneof_enum);
						
						// 2. Going from that property position - N, go through the properties list and retrieve types of corresponding properties
						var oneof_field = new OneofField(oneof_enum.Name);
						
						for (int j = 0; j < oneof_variants.Count; j++) {
							var prop = properties[idx - oneof_variants.Count + j];
							var pt = prop.PropertyType;
							
							// Bonus magic trick: rename obfuscated types and fields based on enum variants
							// This doesn't really matter because we won't use obfuscated assembly, only deobfuscated one
							// But still, maybe one day...
							var enum_var_name = oneof_enum.Fields[j + 2].Name; // First field is '__value', next is 'None'
							
							if (pt.FullName.IsBeeObfuscated() && !enum_var_name.IsBeeObfuscated()) {
								fields[i].Name = enum_var_name + "FieldNumber";
								prop.Name = enum_var_name;
								pt.Name = enum_var_name;
							}
							
							oneof_field.AddRecord(oneof_variants[j], pt.Resolve());
						}
						#else
						var oneof_field = new OneofField(oneof_enum.Name);
						
						for (int j = 0; j < oneof_variants.Count; j++) {
							var prop = properties[prop_seq_number + j];
							var pt = prop.PropertyType;
							
							// Bonus magic trick: rename obfuscated types and fields based on enum variants
							// This doesn't really matter because we won't use obfuscated assembly, only deobfuscated one
							// But still, maybe one day...
							var enum_var_name = oneof_enum.Fields[j + 2].Name; // First field is '__value', next is 'None'
							
							/*if (!enum_var_name.IsBeeObfuscated()) {
								if (pt.FullName.IsBeeObfuscated()) {
									pt.Name = enum_var_name;
								}
								
								if (fields[i+j].Name.IsBeeObfuscated()) {
									fields[i+j].Name = enum_var_name + "FieldNumber";
								}
								
								if (prop.Name.IsBeeObfuscated()) {
									prop.Name = enum_var_name;
								}
							}*/
							
							oneof_field.AddRecord(oneof_variants[j], pt.Resolve());
						}
						#endif
						//WriteLine("Loaded {0} oneof variants", oneof_variants.Count);
						result.Add(field_seq_number++, oneof_field);
						i += oneof_variants.Count-1;
						prop_seq_number += oneof_variants.Count;
					} else {
						throw new ArgumentException(string.Format("Incorrect field {0} follows {1} in {2}", f2.Name, f1.Name, type.Name));
					}
				}
			}
			
			return result;
		}
		
		public void load_obf_assembly(string filename, string obf_base_class, string obf_cmd_id) {
			AddNTEntry(obf_cmd_id, de_cmd_id_name);
			
			obf_base_class_name = obf_base_class;

			obf_assembly = load_assembly(filename);
			
			obf_types = load_pb_packet_types_from_assembly(obf_assembly, obf_base_class, obf_cmd_id);
		}
		
		public void load_de_assembly(string filename) {			
			de_assembly = load_assembly(filename);
			
			de_types = load_pb_packet_types_from_assembly(de_assembly, de_base_class_name, de_cmd_id_name);
		}
		
		private AssemblyDefinition load_assembly(string filename) {
			var resolver = new DefaultAssemblyResolver();
			resolver.AddSearchDirectory(Path.GetDirectoryName(filename));
			
			return AssemblyDefinition.ReadAssembly(filename, new ReaderParameters { AssemblyResolver = resolver });
		}
		
		public void perform_fixup() {				
			foreach (var kvp in de_types) {
				var de_cmd_id = kvp.Key;
				var de_type = kvp.Value;
				
				if (!cmdid_mapping.ContainsKey(de_cmd_id)) {
					WriteLine("WARN: Old packet {0} / {1} does not map to anything!", de_cmd_id, de_type.Name);
					continue;
				}
				
				var obf_cmd_id = cmdid_mapping[de_cmd_id];
				var obf_type = obf_types[obf_cmd_id.Item1];
				
				SetCmdId(de_type, de_cmd_id_name, obf_cmd_id.Item1);
				
				map_protobuf_type(obf_type, de_type, true);
			}
		}
		
		public void save(string filename) {			
			var r = new Random();
			
			de_assembly.Name = new AssemblyNameDefinition("FixedFields", new Version(r.Next(), r.Next(), r.Next(), r.Next()));
			
			de_assembly.Write(filename);
		}
		
		private bool is_protobuf_field(FieldDefinition f) {
			return f.IsPublic && f.HasConstant && f.IsStatic && f.FieldType.FullName.Equals(typeof(int).FullName);
		}
		
		private TypeDefinition[] GetTypes(AssemblyDefinition assembly)
		{
			return assembly.MainModule.Types.ToArray();
		}
		
		private TypeDefinition[] GetProtobufPacketTypes(AssemblyDefinition assembly, string base_class_name, string cmd_id_field_name)
		{
			return GetTypes(assembly).OrderBy(t => t.Name).Where(t => IsProtobufPacket(t, base_class_name, cmd_id_field_name)).ToArray();
		}
		
		private TypeDefinition GetProtobufPacketEnum(TypeDefinition t, string base_class_name, string cmd_id_field_name) {
			var base_type = t.BaseType;
			
			if (base_type == null || base_type.FullName.Split('.').Last() != base_class_name)
				return null;
			
			// There should exist nested type with nested enum with element "CmdId"
			foreach (var nested_type in t.NestedTypes)
			{
				foreach (var inner_type in nested_type.NestedTypes)
				{
					if (inner_type.IsEnum) 
					{
						foreach (var field in inner_type.Fields)
						{
							if (field.Name == cmd_id_field_name)
								return inner_type;
						}
					}
				}
			}
			
			return null;
		}
		
		private bool IsProtobufPacket(TypeDefinition t, string base_class_name, string cmd_id_field_name)
		{
			//this was the case for 1.4.51, but sadly not anymore
			//return t.FullName.StartsWith("Proto.") && !t.FullName.Contains("+");
			
			return GetProtobufPacketEnum(t, base_class_name, cmd_id_field_name) != null;
		}
		
		private bool IsProtobufType(TypeDefinition t, string base_class_name) {
			var base_type = t.BaseType;
			
			if (base_type == null || base_type.FullName.Split('.').Last() != base_class_name)
				return false;
			
			return true;
		}
		
		private uint GetCmdId(TypeDefinition t, string cmd_id_field_name)
		{
			foreach (var nested_type in t.NestedTypes)
			{
				foreach (var inner_type in nested_type.NestedTypes)
				{
					if (inner_type.IsEnum) 
					{
						foreach (var field in inner_type.Fields)
						{
							if (field.Name == cmd_id_field_name)
								return field.GetConstant();
						}
					}
				}
			}
			
			throw new ArgumentException();
		}
		
		private void SetCmdId(TypeDefinition t, string cmd_id_field_name, uint cmd_id) {
			foreach (var nested_type in t.NestedTypes)
			{
				foreach (var inner_type in nested_type.NestedTypes)
				{
					if (inner_type.IsEnum) 
					{
						foreach (var field in inner_type.Fields)
						{
							if (field.Name == cmd_id_field_name) {
								field.Constant = cmd_id;
								return;
							}
						}
					}
				}
			}
			
			throw new ArgumentException();
		}
		
		private uint GetTypeToken(TypeDefinition t)
		{
			foreach (var attrib in t.CustomAttributes)
			{
				if (attrib.AttributeType.Name == token_attrib_name)
				{
					var token = attrib.Fields[0].Argument.Value.ToString();
					return Convert.ToUInt32(token, 16);
				}
			}
			
			throw new ArgumentException();
		}
		
		private uint GetToken(IMemberDefinition f)
		{
			foreach (var attrib in f.CustomAttributes)
			{
				if (attrib.AttributeType.Name == token_attrib_name)
				{
					var token = attrib.Fields[0].Argument.Value.ToString();
					return Convert.ToUInt32(token, 16);
				}
			}
			
			throw new ArgumentException();
		}
		
		private void AssignConstant(FieldReference de_field, object obj) {			
			de_field.Resolve().Constant = obj;
		}
		
		private void WriteLine(string format, params object[] parameters) {
			Console.WriteLine("".PadLeft(debug_level * 4) + format, parameters);
		}
	}
}
